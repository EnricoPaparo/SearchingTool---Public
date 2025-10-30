using Newtonsoft.Json.Linq;
using SearchingTool.Models;
using SearchingTool.Services.Interfaces;
using SearchingTool.Utils;

namespace SearchingTool.Services
{
    public class ZenodoServices : ISearchService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<ZenodoServices> _logger;
        private readonly PortalResolver _portalResolver;

        public string SourceName => "Zenodo";

        public ZenodoServices(HttpClient httpClient, IConfiguration configuration, ILogger<ZenodoServices> logger, PortalResolver portalResolver)
        {
            _httpClient = httpClient;
            _logger = logger;
            _portalResolver = portalResolver;
            _apiKey = configuration["ZenodoAPI:ApiKey"] ?? throw new ArgumentNullException(nameof(configuration), "Zenodo API key is missing.");
        }

        public async Task<List<Publication>> SearchAsync(string query, int startYear, bool downloadPdf, int batchSize, int pageNumber, CancellationToken token = default)
        {
            return await SearchZenodo(query, downloadPdf, batchSize, pageNumber, startYear);
        }

        public async Task<List<Publication>> SearchZenodo(string query, bool downloadPdfToCountPagesOf, int batchSize, int pageNumber, int startYear)
        {
            var results = new List<Publication>();
            var authorCache = new Dictionary<string, Author>();
            int page = 1;
            int validCount = 0;
            int maxWanted = batchSize * pageNumber;

            int nonEnglishCount = 0, dateFilteredCount = 0, badAbstractCount = 0, totalProcessed = 0;

            if (batchSize > 100) batchSize = 100;

            _logger.LogInformation("Zenodo: starting smart search with query: \"{Query}\" and start year: {StartYear}", query, startYear);

            try
            {
                var portal = await _portalResolver.GetPortalAsync(SourceName);

                while (validCount < maxWanted)
                {
                    string searchUrl = $"https://zenodo.org/api/records?q={Uri.EscapeDataString(query)}&size=100&page={page}&access_token={_apiKey}";
                    var searchResponse = await _httpClient.GetStringAsync(searchUrl);
                    var searchDoc = JObject.Parse(searchResponse);
                    var hits = searchDoc["hits"]?["hits"] as JArray;

                    if (hits == null || hits.Count == 0)
                    {
                        _logger.LogInformation("Zenodo: no more records at page {Page}.", page);
                        break;
                    }

                    _logger.LogInformation("Zenodo: API returned {Count} results for page {Page}.", hits.Count, page);

                    foreach (var record in hits)
                    {
                        try
                        {
                            totalProcessed++;

                            var metadata = record["metadata"];
                            if (metadata == null)
                                continue;

                            string? language = metadata["language"]?.ToString()?.ToLower();
                            if (language != "eng" && language != "en")
                            {
                                nonEnglishCount++;
                                continue;
                            }

                            var publicationDateStr = metadata["publication_date"]?.ToString();
                            if (!DateTime.TryParse(publicationDateStr, out var parsedDate) || parsedDate.Year < startYear)
                            {
                                dateFilteredCount++;
                                continue;
                            }

                            string abstractText = metadata["description"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(abstractText) || abstractText.ToLower().Contains("n/a"))
                            {
                                badAbstractCount++;
                                continue;
                            }

                            string? doi = record["doi"]?.ToString();
                            string? doiUrl = record["doi_url"]?.ToString();

                            string? pdfUrl = null;
                            string? pdfText = string.Empty;
                            int? pageCount = null;

                            var filesArray = record["files"] as JArray;
                            if (filesArray != null)
                            {
                                var fulltextFile = filesArray
                                    .FirstOrDefault(f => f["key"]?.ToString() == "fulltext.pdf");

                                if (fulltextFile?["links"]?["self"] != null)
                                {
                                    pdfUrl = fulltextFile["links"]["self"]?.ToString();
                                }
                            }

                            if (!string.IsNullOrEmpty(pdfUrl) && downloadPdfToCountPagesOf)
                            {
                                byte[]? pdf = await Helper.DownloadPdfFromUrlAsync(pdfUrl);
                                if (pdf != null)
                                {
                                    pageCount = Helper.CountPdfPages(pdf);
                                    pdfText = Helper.ExtractPdfText(pdf);
                                }
                                else
                                {
                                    _logger.LogWarning("Zenodo: failed to download PDF from: {Url}", pdfUrl);
                                    pdfUrl = string.Empty;
                                }
                            }

                            var creators = metadata["creators"] as JArray;
                            var publicationAuthors = new List<PublicationAuthor>();
                            if (creators != null)
                            {
                                foreach (var creator in creators)
                                {
                                    var fullName = creator["name"]?.ToString()?.Trim();
                                    if (string.IsNullOrWhiteSpace(fullName)) continue;

                                    if (!authorCache.TryGetValue(fullName, out var author))
                                    {
                                        author = new Author { Name = fullName };
                                        authorCache[fullName] = author;
                                    }

                                    publicationAuthors.Add(new PublicationAuthor { Author = author });
                                }
                            }

                            string tempIssn = metadata["journal_issn"]?.ToString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(tempIssn))
                            {
                                tempIssn = Helper.NormalizeIssn(tempIssn);
                            }

                            var publication = new Publication
                            {
                                Title = metadata["title"]?.ToString() ?? string.Empty,
                                Abstract = abstractText,
                                Doi = doi ?? string.Empty,
                                PdfUrl = pdfUrl ?? string.Empty,
                                PdfText = pdfText ?? string.Empty,
                                Issn = tempIssn,
                                Pages = pageCount?.ToString(),
                                PublicationYear = parsedDate.Year,
                                PublicationMonth = parsedDate.Month,
                                PublicationDay = parsedDate.Day,
                                Portal = portal,
                                PublicationsAuthors = publicationAuthors
                            };

                            results.Add(publication);
                            validCount++;

                            if (validCount >= maxWanted)
                                break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Zenodo: error processing a record.");
                        }
                    }

                    page++;
                    await Task.Delay(1000);
                }

                _logger.LogInformation("Zenodo: search completed. Total pages fetched: {Page}", page - 1);
                _logger.LogInformation("Zenodo: valid publications collected: {Count}", results.Count);
                _logger.LogInformation("Zenodo: total records processed: {Total}", totalProcessed);
                _logger.LogInformation("Zenodo: discarded {Lang} for language, {Date} for publication date, {Abstract} for empty or invalid abstract.",
                    nonEnglishCount, dateFilteredCount, badAbstractCount);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Zenodo: HTTP request error during search.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zenodo: unexpected error during search.");
            }

            return results;
        }


    }
}
