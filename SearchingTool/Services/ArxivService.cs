using SearchingTool.Models;
using SearchingTool.Services.Interfaces;
using SearchingTool.Utils;
using System.Xml.Linq;

namespace SearchingTool.Services
{
    public class ArxivService : ISearchService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ArxivService> _logger;
        private readonly PortalResolver _portalResolver;

        public string SourceName => "ArXiv";

        public ArxivService(HttpClient httpClient, ILogger<ArxivService> logger, PortalResolver portalResolver)
        {
            _httpClient = httpClient;
            _logger = logger;
            _portalResolver = portalResolver;
        }

        public async Task<List<Publication>> SearchAsync(string query, int startYear, bool downloadPdf, int batchSize, int pageNumber = 1, CancellationToken token = default)
        {
            return await SearchArxiv(query, downloadPdf, batchSize, startYear);
        }

        public async Task<List<Publication>> SearchArxiv(string query, bool downloadPdfToCountPagesOf, int batchSize, int startYear)
        {
            var results = new List<Publication>();
            var authorCache = new Dictionary<string, Author>();
            int start = 0;
            bool hasMoreResults = true;

            _logger.LogInformation("ArXiv: starting search with query: \"{Query}\" and start year: {StartYear}", query, startYear);

            try
            {
                var portal = await _portalResolver.GetPortalAsync(SourceName);

                while (hasMoreResults && results.Count < batchSize)
                {
                    string searchUrl =
                        $"http://export.arxiv.org/api/query?" +
                        $"search_query={Uri.EscapeDataString(query)}" +
                        $"&start={start}" +
                        $"&max_results={batchSize}" +
                        $"&sortBy=submittedDate" +      
                        $"&sortOrder=descending";       

                    var searchResponse = await _httpClient.GetStringAsync(searchUrl);

                    if (string.IsNullOrEmpty(searchResponse))
                    {
                        _logger.LogWarning("ArXiv: received empty response.");
                        break;
                    }

                    var xmlDoc = XDocument.Parse(searchResponse);
                    var entries = xmlDoc.Descendants("{http://www.w3.org/2005/Atom}entry");

                    if (!entries.Any())
                    {
                        _logger.LogInformation("ArXiv: no entries returned.");
                        hasMoreResults = false;
                        break;
                    }

                    foreach (var entry in entries)
                    {
                        try
                        {
                            var title = entry.Element("{http://www.w3.org/2005/Atom}title")?.Value?.Trim() ?? "";
                            var summary = entry.Element("{http://www.w3.org/2005/Atom}summary")?.Value?.Trim() ?? "";
                            var authors = entry.Elements("{http://www.w3.org/2005/Atom}author");

                            var publicationAuthors = new List<PublicationAuthor>();
                            foreach (var authorNode in authors)
                            {
                                var fullName = authorNode.Element("{http://www.w3.org/2005/Atom}name")?.Value?.Trim();
                                if (string.IsNullOrWhiteSpace(fullName)) continue;

                                if (!authorCache.TryGetValue(fullName, out var author))
                                {
                                    author = new Author { Name = fullName };
                                    authorCache[fullName] = author;
                                }

                                publicationAuthors.Add(new PublicationAuthor { Author = author });
                            }

                            var published = entry.Element("{http://www.w3.org/2005/Atom}published")?.Value;
                            int? pubYear = null, pubMonth = null, pubDay = null;
                            if (DateTime.TryParse(published, out var pubDate))
                            {
                                pubYear = pubDate.Year;
                                pubMonth = pubDate.Month;
                                pubDay = pubDate.Day;
                            }

                            if (pubYear == null || pubYear < startYear)
                            {
                                _logger.LogDebug("ArXiv: skipping article from year {Year}", pubYear ?? 0);
                                continue;
                            }

                            var rawId = entry.Element("{http://www.w3.org/2005/Atom}id")?.Value ?? "";
                            string normalizedDoi = rawId.Contains("/abs/") ? rawId.Split("/abs/").LastOrDefault() ?? "" : rawId;

                            var pdfUrl = entry.Elements("{http://www.w3.org/2005/Atom}link")
                                              .FirstOrDefault(l => l.Attribute("title")?.Value == "pdf")
                                              ?.Attribute("href")?.Value ?? string.Empty;

                            string? pdfText = string.Empty;
                            int? pageCount = null;

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
                                    _logger.LogWarning("ArXiv: failed to download PDF from: {Url}", pdfUrl);
                                    pdfUrl = string.Empty;
                                }
                            }

                            var publication = new Publication
                            {
                                Doi = normalizedDoi,
                                Title = title,
                                Abstract = summary,
                                PublicationYear = pubYear,
                                PublicationMonth = pubMonth,
                                PublicationDay = pubDay,
                                Portal = portal,
                                Issn = string.Empty,
                                PdfUrl = pdfUrl,
                                PdfText = pdfText,
                                Pages = pageCount?.ToString(),
                                PublicationsAuthors = publicationAuthors
                            };

                            results.Add(publication);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "ArXiv: error processing article.");
                        }
                    }

                    start += batchSize;
                    await Task.Delay(500);
                }

                _logger.LogInformation("ArXiv: search completed. Publications found: {Count}", results.Count);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "ArXiv: HTTP request error.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArXiv: unexpected error during search.");
            }

            return results;
        }
    }
}
