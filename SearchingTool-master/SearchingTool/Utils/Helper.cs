using System.Globalization;
using HtmlAgilityPack;
using SearchingTool.Models;
using UglyToad.PdfPig;
using CsvHelper;
using CsvHelper.Configuration;
using System.Text.RegularExpressions;
using System.Data;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Text.Json;
using System.Text;
using Serilog;
using SearchingTool.Data;

namespace SearchingTool.Utils
{
    public static class Helper
    {
        public static async Task<string?> GetPdfUrlFromHtmlAsync(string articleUrl)
        {
            if (string.IsNullOrWhiteSpace(articleUrl))
                return null;

            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
                string htmlContent = await client.GetStringAsync(articleUrl);

                HtmlDocument doc = new();
                doc.LoadHtml(htmlContent);

                string? pdfUrl = doc.DocumentNode.SelectSingleNode("//a[contains(@href, '.pdf')]")
                    ?.GetAttributeValue("href", "");

                if (string.IsNullOrEmpty(pdfUrl))
                {
                    var metaPdfNode = doc.DocumentNode
                        .SelectSingleNode("//meta[contains(@name, 'citation_pdf_url')]");
                    string? metaPdfUrl = metaPdfNode?.GetAttributeValue("content", "");

                    if (!string.IsNullOrEmpty(metaPdfUrl) && metaPdfUrl.EndsWith(".pdf"))
                    {
                        pdfUrl = metaPdfUrl;
                    }
                }

                if (!string.IsNullOrEmpty(pdfUrl) && !pdfUrl.StartsWith("http"))
                {
                    pdfUrl = new Uri(new Uri(articleUrl), pdfUrl).ToString();
                }

                return pdfUrl;
            }
            catch (Exception ex)
            {
                Log.Error($"{ex.Message}");
                return null;
            }
        }

        public static async Task<byte[]?> DownloadPdfFromUrlAsync(string pdfUrl)
        {
            using HttpClient client = new HttpClient();
            try
            {
                byte[] pdfBytes = await client.GetByteArrayAsync(pdfUrl);
                return pdfBytes;
            }
            catch (Exception ex)
            {
                Log.Error($"{ex.Message}");
                return null;
            }
        }

        public static int? CountPdfPages(byte[] pdfBytes)
        {
            try
            {
                using MemoryStream stream = new MemoryStream(pdfBytes);
                using PdfDocument document = PdfDocument.Open(stream);
                return document.NumberOfPages;
            }
            catch (Exception ex)
            {
                Log.Error($"{ex.Message}");
                return null;
            }
        }

        public static string? ExtractPdfText(byte[] pdfBytes)
        {
            try
            {
                using MemoryStream stream = new MemoryStream(pdfBytes);
                using PdfDocument document = PdfDocument.Open(stream);

                StringBuilder text = new StringBuilder();
                foreach (var page in document.GetPages())
                {
                    text.AppendLine(page.Text);
                }

                return text.ToString();
            }
            catch (Exception ex)
            {
                Log.Error($"{ex.Message}");
                return null;
            }
        }

        public static string? TransformDoiUrl(string doiUrl)
        {
            if (string.IsNullOrWhiteSpace(doiUrl) || !doiUrl.Contains("zenodo", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int zenodoIndex = doiUrl.IndexOf("zenodo", StringComparison.OrdinalIgnoreCase);
            string recordId = doiUrl.Substring(zenodoIndex + "zenodo.".Length);
            string newUrl = $"https://zenodo.org/records/{recordId}";

            return newUrl;
        }

        public static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("Empty URL");
            }

            int pdfIndex = url.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase);

            if (pdfIndex == -1 || pdfIndex == url.Length - ".pdf".Length)
            {
                return url;
            }

            url = url.Substring(0, pdfIndex + ".pdf".Length);

            int lastSlashIndex = url.LastIndexOf('/');

            if (lastSlashIndex == -1)
            {
                return url;
            }

            string baseUrl = url.Substring(0, lastSlashIndex + 1);
            string fileName = url.Substring(lastSlashIndex + 1);
            string encodedFileName = Uri.EscapeDataString(fileName);
            string cleanedUrl = baseUrl + encodedFileName;

            return cleanedUrl;
        }

        public static async Task EnsurePortalsSeededAsync(ScopingReviewContext context)
        {
            var defaultPortals = new List<string> { "PubMed", "ArXiv", "Zenodo", "Scopus", "Unknown" };

            var existingDescriptions = await context.Portals
                .Select(p => p.Description)
                .ToListAsync();

            var existingSet = new HashSet<string>(existingDescriptions, StringComparer.OrdinalIgnoreCase);

            var newPortals = defaultPortals
                .Where(p => !existingSet.Contains(p))
                .Select(p => new Portal { Description = p })
                .ToList();

            if (newPortals.Any())
            {
                context.Portals.AddRange(newPortals);
                await context.SaveChangesAsync();
                Log.Information("Portals seeded: {Portals}", string.Join(", ", newPortals.Select(p => p.Description)));
            }
            else
            {
                Log.Information("All required portals already exist.");
            }
        }

        public static List<Journal> ExtractJournals(string folderPath)
        {
            var journalsDict = new Dictionary<string, Journal>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(folderPath, "*.csv"))
            {
                var year = ExtractYearFromFileName(file);
                Log.Information("Processing file: {FileName} (Year: {Year})", Path.GetFileName(file), year);

                using var reader = new StreamReader(file);
                var config = new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    Delimiter = ";",
                    MissingFieldFound = null,
                    HeaderValidated = null,
                };

                using var csv = new CsvHelper.CsvReader(reader, config);
                csv.Read();
                csv.ReadHeader();

                while (csv.Read())
                {
                    var issnRaw = csv.GetField("Issn");
                    var categoriesRaw = csv.GetField("Categories");

                    if (string.IsNullOrWhiteSpace(issnRaw) || string.IsNullOrWhiteSpace(categoriesRaw))
                        continue;

                    var issnList = issnRaw
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(NormalizeIssn)
                        .Where(issn => !string.IsNullOrWhiteSpace(issn))
                        .Distinct();

                    var categoryList = categoriesRaw
                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => ParseCategoryAndQuartile(c.Trim()))
                        .Where(cc => cc != null)
                        .Select(cc => cc!)
                        .ToList();

                    foreach (var issn in issnList)
                    {
                        if (!journalsDict.TryGetValue(issn, out var journal))
                        {
                            journal = new Journal
                            {
                                Issn = issn,
                                Categories = new List<CategoryWithQuartile>()
                            };
                            journalsDict[issn] = journal;
                        }

                        foreach (var item in categoryList)
                        {
                            bool alreadyPresent = journal.Categories
                                .Any(c => c.CategoryName.Equals(item.CategoryName, StringComparison.OrdinalIgnoreCase)
                                       && c.Quartile.Equals(item.Quartile, StringComparison.OrdinalIgnoreCase));

                            if (!alreadyPresent)
                            {
                                journal.Categories.Add(new CategoryWithQuartile
                                {
                                    CategoryName = item.CategoryName,
                                    Quartile = item.Quartile
                                });
                            }
                        }
                    }
                }
            }

            Log.Information("Journal category extraction completed. Total ISSNs: {Count}", journalsDict.Count);

            return journalsDict.Values.ToList();
        }

        public static List<Publication> GetCategoriesForPublications(List<Publication> publications, List<Journal> journals)
        {
            foreach (var publication in publications)
            {
                if (string.IsNullOrWhiteSpace(publication.Issn))
                    continue;

                var matchedJournal = journals
                    .FirstOrDefault(j => string.Equals(j.Issn, publication.Issn, StringComparison.OrdinalIgnoreCase));

                if (matchedJournal == null || matchedJournal.Categories == null)
                    continue;

                publication.PublicationsCategories ??= new List<PublicationCategory>();

                foreach (var cat in matchedJournal.Categories)
                {
                    bool alreadyAssigned = publication.PublicationsCategories
                        .Any(pc => string.Equals(pc.Category.CategoryName, cat.CategoryName, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyAssigned)
                    {
                        publication.PublicationsCategories.Add(new PublicationCategory
                        {
                            Category = new Category { CategoryName = cat.CategoryName },
                            Quartile = cat.Quartile
                        });
                    }
                }
            }

            return publications;
        }

        public static List<Publication> Clean(List<Publication> publications)
        {
            int initialCount = publications.Count;
            var cleaned = RemoveDuplicatesAndMergeByDoi(publications);
            int afterDedupCount = cleaned.Count;

            cleaned = cleaned
                .Where(pub =>
                    !string.IsNullOrWhiteSpace(pub.Abstract) &&
                    pub.PublicationsAuthors != null &&
                    pub.PublicationsAuthors.Any(pa => !string.IsNullOrWhiteSpace(pa.Author?.Name))
                )
                .ToList();

            int finalCount = cleaned.Count;
            int removedForContent = afterDedupCount - finalCount;

            Log.Information("Clean: Started with {InitialCount}, after deduplication {AfterDedupCount}, removed {RemovedForContent} for missing abstract/authors, final count {FinalCount}",
                initialCount, afterDedupCount, removedForContent, finalCount);

            return cleaned;
        }

        public static List<Publication> RemoveDuplicatesAndMergeByDoi(List<Publication> publications)
        {
            var mergedPublications = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase);
            int duplicateCount = 0;

            foreach (var pub in publications)
            {
                if (string.IsNullOrWhiteSpace(pub.Doi))
                    continue;

                if (!mergedPublications.TryGetValue(pub.Doi, out var existing))
                {
                    mergedPublications[pub.Doi] = pub;
                }
                else
                {
                    duplicateCount++;

                    Log.Information("Duplicate DOI detected: {Doi} | Keeping Portal: {ExistingPortal}, Skipping Portal: {NewPortal}",
                        existing.Doi,
                        existing.Portal?.Description ?? "Unknown",
                        pub.Portal?.Description ?? "Unknown");

                    existing.Title = string.IsNullOrWhiteSpace(existing.Title) ? pub.Title : existing.Title;
                    existing.Abstract = string.IsNullOrWhiteSpace(existing.Abstract) ? pub.Abstract : existing.Abstract;
                    existing.PdfUrl = string.IsNullOrWhiteSpace(existing.PdfUrl) ? pub.PdfUrl : existing.PdfUrl;
                    existing.Portal = (existing.Portal == null || string.IsNullOrWhiteSpace(existing.Portal.Description)) ? pub.Portal : existing.Portal;
                    existing.Issn = string.IsNullOrWhiteSpace(existing.Issn) ? pub.Issn : existing.Issn;
                    existing.Pages ??= pub.Pages;
                    existing.PublicationYear ??= pub.PublicationYear;
                    existing.PublicationMonth ??= pub.PublicationMonth;
                    existing.PublicationDay ??= pub.PublicationDay;

                    if (existing.PublicationsAuthors != null && pub.PublicationsAuthors != null)
                    {
                        var existingAuthorNames = existing.PublicationsAuthors
                            .Select(pa => pa.Author.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var newAuthors = pub.PublicationsAuthors
                            .Where(pa => !existingAuthorNames.Contains(pa.Author.Name))
                            .ToList();

                        existing.PublicationsAuthors.AddRange(newAuthors);
                    }

                    if (existing.PublicationsCategories != null && pub.PublicationsCategories != null)
                    {
                        var existingCatKeys = existing.PublicationsCategories
                            .Select(pc => (pc.Category.CategoryName, pc.Quartile))
                            .ToHashSet();

                        var newCats = pub.PublicationsCategories
                            .Where(pc => !existingCatKeys.Contains((pc.Category.CategoryName, pc.Quartile)))
                            .ToList();

                        existing.PublicationsCategories.AddRange(newCats);
                    }
                }
            }

            Log.Information("RemoveDuplicatesAndMergeByDoi: Started with {InitialCount} publications, found {DuplicateCount} duplicate(s), final unique publications: {UniqueCount}",
                publications.Count, duplicateCount, mergedPublications.Count);

            return mergedPublications.Values.ToList();
        }

        public static async Task InsertPublicationsAsync(List<Publication> publications, int searchId, ScopingReviewContext context)
        {
            // Cache iniziali
            var portalCache = await context.Portals
                .GroupBy(p => p.Description)
                .Select(g => g.First())
                .ToDictionaryAsync(p => p.Description);

            var authorCache = await context.Authors
                .GroupBy(a => a.Name)
                .Select(g => g.First())
                .ToDictionaryAsync(a => a.Name);

            var categoryCache = await context.Categories
                .GroupBy(c => c.CategoryName)
                .Select(g => g.First())
                .ToDictionaryAsync(c => c.CategoryName);

            var publicationDoiCache = await context.Publications
                .Where(p => !string.IsNullOrEmpty(p.Doi))
                .GroupBy(p => p.Doi.ToLower())
                .Select(g => g.First())
                .ToDictionaryAsync(p => p.Doi.ToLower());

            var publicationTitleCache = context.Publications
                .Where(p => !string.IsNullOrEmpty(p.Title))
                .AsEnumerable()
                .GroupBy(p => NormalizeTitle(p.Title))
                .Select(g => g.First())
                .ToDictionary(p => NormalizeTitle(p.Title));

            Log.Information("Inserting publications into database...");

            foreach (var pub in publications)
            {
                try
                {
                    if (pub == null || (string.IsNullOrWhiteSpace(pub.Doi) && string.IsNullOrWhiteSpace(pub.Title)))
                        continue;

                    int? normYear = pub.PublicationYear > 0 ? pub.PublicationYear : (int?)null;
                    int? normMonth = pub.PublicationMonth is >= 1 and <= 12 ? pub.PublicationMonth : (int?)null;
                    int? normDay = pub.PublicationDay is >= 1 and <= 31 ? pub.PublicationDay : (int?)null;

                    Publication existingPub = null;

                    if (!string.IsNullOrWhiteSpace(pub.Doi))
                        publicationDoiCache.TryGetValue(pub.Doi.ToLower(), out existingPub);
                    else if (!string.IsNullOrWhiteSpace(pub.Title))
                        publicationTitleCache.TryGetValue(NormalizeTitle(pub.Title), out existingPub);

                    int publicationId;
                    Portal portal;

                    // --- PORTAL --- //
                    if (!string.IsNullOrWhiteSpace(pub.Portal?.Description)
                        && portalCache.TryGetValue(pub.Portal.Description, out var cachedPortal))
                    {
                        portal = cachedPortal;
                        if (context.Entry(portal).State == EntityState.Detached)
                            context.Attach(portal);
                    }
                    else
                    {
                        portal = portalCache.GetValueOrDefault("Unknown") ?? new Portal { Description = "Unknown" };
                        if (context.Entry(portal).State == EntityState.Detached)
                            context.Attach(portal);
                    }

                    // --- PUBLICATION --- //
                    if (existingPub != null)
                    {
                        await context.Entry(existingPub)
                            .Collection(p => p.PublicationsCategories)
                            .LoadAsync();

                        context.PublicationsCategories.RemoveRange(existingPub.PublicationsCategories);

                        existingPub.Doi = pub.Doi;
                        existingPub.Title = pub.Title;
                        existingPub.Abstract = pub.Abstract;
                        existingPub.PublicationYear = normYear;
                        existingPub.PublicationMonth = normMonth;
                        existingPub.PublicationDay = normDay;
                        existingPub.Issn = pub.Issn;
                        existingPub.PdfUrl = pub.PdfUrl;
                        existingPub.PdfText = pub.PdfText;
                        existingPub.Pages = pub.Pages;
                        existingPub.Portal = portal;

                        publicationId = existingPub.Id;
                    }
                    else
                    {
                        var newPub = new Publication
                        {
                            Doi = pub.Doi,
                            Title = pub.Title,
                            Abstract = pub.Abstract,
                            PublicationYear = normYear,
                            PublicationMonth = normMonth,
                            PublicationDay = normDay,
                            Issn = pub.Issn,
                            PdfUrl = pub.PdfUrl,
                            PdfText = pub.PdfText,
                            Pages = pub.Pages,
                            Portal = portal
                        };

                        context.Publications.Add(newPub);
                        await context.SaveChangesAsync();
                        publicationId = newPub.Id;
                        existingPub = newPub;
                    }

                    // --- AUTHORS --- //
                    if (pub.PublicationsAuthors != null)
                    {
                        var uniqueAuthors = pub.PublicationsAuthors
                            .Where(pa => !string.IsNullOrWhiteSpace(pa.Author?.Name))
                            .Select(pa => pa.Author.Name.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var authorName in uniqueAuthors)
                        {
                            if (!authorCache.TryGetValue(authorName, out var author))
                            {
                                author = new Author { Name = authorName };
                                context.Authors.Add(author);
                                await context.SaveChangesAsync();
                                authorCache[authorName] = author;
                            }

                            bool existsLocal = context.PublicationsAuthors.Local.Any(x =>
                                x.PublicationId == publicationId && x.AuthorId == author.Id);

                            bool existsDb = await context.PublicationsAuthors
                                .AnyAsync(x => x.PublicationId == publicationId && x.AuthorId == author.Id);

                            if (!existsLocal && !existsDb)
                            {
                                context.PublicationsAuthors.Add(new PublicationAuthor
                                {
                                    PublicationId = publicationId,
                                    AuthorId = author.Id
                                });
                            }
                        }
                    }

                    // --- CATEGORIES --- //
                    if (pub.PublicationsCategories != null)
                    {
                        var uniqueCategories = pub.PublicationsCategories
                            .Where(pc => !string.IsNullOrWhiteSpace(pc.Category?.CategoryName))
                            .Select(pc => pc.Category.CategoryName.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var categoryName in uniqueCategories)
                        {
                            if (!categoryCache.TryGetValue(categoryName, out var category))
                            {
                                category = new Category { CategoryName = categoryName };
                                context.Categories.Add(category);
                                await context.SaveChangesAsync();
                                categoryCache[categoryName] = category;
                            }

                            bool existsLocal = context.PublicationsCategories.Local.Any(x =>
                                x.PublicationId == publicationId && x.CategoryId == category.Id);

                            bool existsDb = await context.PublicationsCategories
                                .AnyAsync(x => x.PublicationId == publicationId && x.CategoryId == category.Id);

                            if (!existsLocal && !existsDb)
                            {
                                context.PublicationsCategories.Add(new PublicationCategory
                                {
                                    PublicationId = publicationId,
                                    CategoryId = category.Id,
                                    Quartile = pub.PublicationsCategories
                                        .FirstOrDefault(pc => pc.Category?.CategoryName == categoryName)?.Quartile
                                });
                            }
                        }
                    }

                    // --- RESULTS --- //
                    bool resultExists = await context.Results
                        .AnyAsync(r => r.PublicationId == publicationId && r.SearchId == searchId);

                    if (!resultExists)
                    {
                        context.Results.Add(new Result
                        {
                            SearchId = searchId,
                            PublicationId = publicationId
                        });
                    }

                    // --- SAVE --- //
                    await context.SaveChangesAsync();
                    Log.Information("Publication processed: {Title}", pub?.Title);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error inserting publication: {Title}", pub?.Title);
                }
            }

            Log.Information("All publications processed.");
        }


        private static string NormalizeTitle(string title)
        {
            return title.Trim().ToLowerInvariant().Replace(" ", "");
        }

        public static async Task<int> SearchInsertAsync(string queryText, int startYear, ScopingReviewContext context)
        {
            var search = new Search
            {
                QueryText = queryText,
                StartYear = startYear,
                SearchDate = DateTime.UtcNow
            };

            context.Searches.Add(search);
            await context.SaveChangesAsync();

            return search.Id;
        }

        private static CategoryWithQuartile? ParseCategoryAndQuartile(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var match = Regex.Match(input, @"^(.*?)\s*\((Q[1-4])\)$");

            if (!match.Success)
            {
                return null;
            }

            var categoryName = match.Groups[1].Value.Trim();
            var quartile = match.Groups[2].Value.Trim().ToUpper();

            return new CategoryWithQuartile
            {
                CategoryName = categoryName,
                Quartile = quartile
            };
        }

        private static int ExtractYearFromFileName(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var match = Regex.Match(fileName, @"\d{4}");
            return match.Success ? int.Parse(match.Value) : 0;
        }

        public static string NormalizeIssn(string issn)
        {
            if (string.IsNullOrWhiteSpace(issn)) return "";

            var digits = new string(issn.Where(char.IsDigit).ToArray());

            if (digits.Length == 8)
                return $"{digits.Substring(0, 4)}-{digits.Substring(4)}".ToUpperInvariant();

            return issn.Replace(" ", "").Trim().ToUpperInvariant();
        }

    }
}