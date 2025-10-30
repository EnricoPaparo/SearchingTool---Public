using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SearchingTool.Data;
using SearchingTool.Models;
using SearchingTool.Services.Interfaces;
using SearchingTool.Utils;
using System.Text;

[ApiController]
[Route("search")]
public class SearchController : ControllerBase
{
    private readonly IEnumerable<ISearchService> _searchServices;
    private readonly IDbContextFactory<ScopingReviewContext> _contextFactory;
    private readonly PathsOptions _pathsOptions;
    private readonly ILogger<SearchController> _logger;
    private const int MaxRetries = 3;

    public SearchController(
        IEnumerable<ISearchService> searchServices,
        IDbContextFactory<ScopingReviewContext> contextFactory,
        IOptions<PathsOptions> pathsOptions,
        ILogger<SearchController> logger)
    {
        _searchServices = searchServices;
        _contextFactory = contextFactory;
        _pathsOptions = pathsOptions.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string query, [FromQuery] int startYear, [FromQuery] int maxResults)
    {
        var logs = new List<string>();
        List<Publication> allPublications = new();

        void Log(string message)
        {
            _logger.LogInformation(message);
            logs.Add(message);
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        try
        {
            int searchId = await Helper.SearchInsertAsync(query, startYear, context);

            int batchSize = Math.Clamp(maxResults, 10, 10000);
            int pageNumber = (int)Math.Ceiling(batchSize / 100.0);
            const bool documentDownload = false;

            Log($"Starting search for: \"{query}\" from year {startYear}");

            var searchTasks = _searchServices.Select(service =>
                RetrySearchAsync(
                    () => service.SearchAsync(query, startYear, documentDownload, batchSize, pageNumber),
                    MaxRetries,
                    service.SourceName,
                    logs)).ToList();

            var allResults = await Task.WhenAll(searchTasks);
            allPublications = allResults.SelectMany(r => r).ToList();

            Log($"Total publications found across services: {allPublications.Count}");

            var journals = Helper.ExtractJournals(_pathsOptions.ScimagoFolder);
            allPublications = Helper.GetCategoriesForPublications(allPublications, journals);
            allPublications = Helper.Clean(allPublications);

            await Helper.InsertPublicationsAsync(allPublications, searchId, context);

            Log($"Search completed successfully. Final publications: {allPublications.Count}");

            return Ok(new
            {
                results = FormatPublications(allPublications),
                logs
            });
        }
        catch (Exception ex)
        {
            string errorMessage = $"Unexpected error during search: {ex.Message}";
            _logger.LogError(ex, errorMessage);
            logs.Add(errorMessage);

            return Ok(new
            {
                results = FormatPublications(allPublications),
                logs
            });
        }
    }

    [HttpDelete("{searchId}")]
    public async Task<IActionResult> DeleteSearch(int searchId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        try
        {
            var exclusivePublicationIds = await context.Results
                .GroupBy(r => r.PublicationId)
                .Where(g => g.Count() == 1 && g.Any(r => r.SearchId == searchId))
                .Select(g => g.Key)
                .ToListAsync();

            var exclusivePublications = await context.Publications
                .Where(p => exclusivePublicationIds.Contains(p.Id))
                .Include(p => p.PublicationsAuthors)
                .Include(p => p.PublicationsCategories)
                .AsSplitQuery()
                .ToListAsync();

            foreach (var pub in exclusivePublications)
            {
                context.PublicationsAuthors.RemoveRange(pub.PublicationsAuthors);
                context.PublicationsCategories.RemoveRange(pub.PublicationsCategories);
            }

            context.Publications.RemoveRange(exclusivePublications);

            var results = await context.Results
                .Where(r => r.SearchId == searchId)
                .ToListAsync();
            context.Results.RemoveRange(results);

            var search = await context.Searches.FindAsync(searchId);
            if (search != null)
            {
                context.Searches.Remove(search);
            }

            await context.SaveChangesAsync();

            return Ok(new { message = "Search and publication deleted." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("lastlog")]
    public IActionResult GetLastLog()
    {
        try
        {
            var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logsDirectory))
            {
                return NotFound("Logs directory not found.");
            }

            var lastLogFile = Directory
                .GetFiles(logsDirectory, "log-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (lastLogFile == null)
            {
                return NotFound("No log files found.");
            }

            using var stream = new FileStream(lastLogFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string logContent = reader.ReadToEnd();

            return Content(logContent, "text/plain");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error reading log file: {ex.Message}");
        }
    }

    private static object FormatPublications(List<Publication> publications)
    {
        return publications.Select(pub => new
        {
            pub.Id,
            pub.Doi,
            pub.Title,
            pub.Abstract,
            pub.PublicationYear,
            pub.PublicationMonth,
            pub.PublicationDay,
            pub.Issn,
            pub.PdfUrl,
            Portal = pub.Portal?.Description,
            Authors = pub.PublicationsAuthors != null
                ? pub.PublicationsAuthors
                    .Select(pa => pa.Author?.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList()
                : new List<string>(),
            Categories = pub.PublicationsCategories != null
                ? pub.PublicationsCategories
                    .Where(pc => pc?.Category != null)
                    .Select(pc => new FormattedCategory
                    {
                        CategoryName = pc.Category.CategoryName,
                        Quartile = pc.Quartile
                    }).ToList()
                : new List<FormattedCategory>()
        });
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetSearchHistory()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var history = context.Searches
            .OrderByDescending(s => s.SearchDate)
            .Select(s => new
            {
                s.Id,
                s.QueryText,
                s.StartYear,
                s.SearchDate
            })
            .ToList();

        var json = JsonConvert.SerializeObject(history, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        });

        return Content(json, "application/json");
    }

    [HttpGet("download/{searchId}")]
    public async Task<IActionResult> DownloadSearchResults(int searchId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var results = await context.Results
            .Include(r => r.Publication)
                .ThenInclude(p => p.Portal)
            .Include(r => r.Publication)
                .ThenInclude(p => p.PublicationsAuthors)
                    .ThenInclude(pa => pa.Author)
            .Include(r => r.Publication)
                .ThenInclude(p => p.PublicationsCategories)
                    .ThenInclude(pc => pc.Category)
            .AsSplitQuery()
            .Where(r => r.SearchId == searchId)
            .ToListAsync();

        if (!results.Any())
            return NotFound("No results found for this search.");

        var publications = results.Select(r => {
            var p = r.Publication;
            return new
            {
                p.Id,
                p.Doi,
                p.Title,
                p.Abstract,
                p.PublicationYear,
                p.PublicationMonth,
                p.PublicationDay,
                p.Issn,
                p.PdfUrl,
                p.Pages,
                SearchId = searchId,
                Portal = p.Portal?.Description ?? "Unknown",
                Authors = p.PublicationsAuthors.Select(pa => pa.Author.Name).ToList(),
                Categories = p.PublicationsCategories.Select(pc => new {
                    pc.Category.CategoryName,
                    pc.Quartile
                }).ToList()
            };
        }).ToList();

        var json = JsonConvert.SerializeObject(publications, Formatting.Indented);
        var fileName = $"Search_{searchId}_Results.json";
        var content = Encoding.UTF8.GetBytes(json);

        return File(content, "application/json", fileName);
    }

    private async Task<List<Publication>> RetrySearchAsync(Func<Task<List<Publication>>> searchFunc, int maxRetries, string sourceName, List<string> logs)
    {
        int retryCount = 0;
        int delay = 1000;

        void Log(string message)
        {
            _logger.LogInformation(message);
            logs.Add(message);
        }

        Log($"Retry active for {sourceName}");

        while (retryCount < maxRetries)
        {
            try
            {
                var results = await searchFunc();
                if (results.Count > 0)
                {
                    Log($"{sourceName} returned {results.Count} results");
                    return results;
                }

                Log($"Attempt {retryCount + 1} failed for {sourceName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during attempt {retryCount + 1} for {sourceName}");
                logs.Add($"Error during attempt {retryCount + 1} for {sourceName}: {ex.Message}");
            }

            retryCount++;
            await Task.Delay(delay);
            delay *= 2;
        }

        Log($"All attempts failed for {sourceName}");
        return new List<Publication>();
    }
}
