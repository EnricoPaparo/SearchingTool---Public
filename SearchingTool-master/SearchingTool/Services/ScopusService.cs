using System.Web;
using Newtonsoft.Json.Linq;
using SearchingTool.Models;
using SearchingTool.Services.Interfaces;
using SearchingTool.Utils;

namespace SearchingTool.Services
{
    public class ScopusService : ISearchService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<ScopusService> _logger;
        private readonly PortalResolver _portalResolver;
        private const int RateLimit = 120;

        public string SourceName => "Scopus";

        public ScopusService(IHttpClientFactory clientFactory, ILogger<ScopusService> logger, PortalResolver portalResolver)
        {
            _clientFactory = clientFactory;
            _logger = logger;
            _portalResolver = portalResolver;
        }

        public async Task<List<Publication>> SearchAsync(string query, int startYear, bool downloadPdf, int batchSize, int pageNumber = 1, CancellationToken token = default)
        {
            return await SearchScopus(query, downloadPdf, batchSize, pageNumber, startYear, downloadPdf);
        }

        public async Task<List<Publication>> SearchScopus(string query, bool downloadPdfToCountPagesOf, int pageSize, int pages, int startYear, bool documentDownload, CancellationToken cancellationToken = default)
        {
            using var httpClient = _clientFactory.CreateClient("Scopus");
            var results = new List<Publication>();
            var authorCache = new Dictionary<string, Author>();
            int page = 1;
            int processedCount = 0;
            int totalPublications = 0;

            if (pages == -1) pages = int.MaxValue;

            _logger.LogInformation("Scopus: starting search with query: \"{Query}\" from year: {StartYear}", query, startYear);
            var portal = await _portalResolver.GetPortalAsync(SourceName);

            do
            {
                try
                {
                    string encodedQuery = HttpUtility.UrlEncode($"{query} AND LANGUAGE(English) AND PUBYEAR > {startYear}");
                    var requestUrl = $"{httpClient.BaseAddress}search/scopus?field=identifier,issn,doi,title&query={encodedQuery}&start={page}&count={pageSize}";

                    _logger.LogInformation("Scopus: requesting page {Page} → {Url}", page, requestUrl);

                    var response = await httpClient.GetAsync(requestUrl, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    var responseObject = JObject.Parse(jsonResponse);

                    totalPublications = int.TryParse(responseObject["search-results"]?["opensearch:totalResults"]?.ToString(), out var total) ? total : 0;
                    var entries = responseObject["search-results"]?["entry"] as JArray;

                    if (entries == null || entries.Count == 0)
                    {
                        _logger.LogWarning("Scopus: no entries returned for page {Page}", page);
                        break;
                    }

                    foreach (var entry in entries)
                    {
                        try
                        {
                            if (entry["error"]?.ToString() == "Result set was empty")
                            {
                                _logger.LogInformation("Scopus: no results found for this query. Skipping parsing.");
                                continue;
                            }

                            var scopusId = entry["dc:identifier"]?.ToString()?.Split(':').LastOrDefault();
                            if (string.IsNullOrEmpty(scopusId))
                            {
                                _logger.LogWarning("Scopus: skipping entry with missing identifier.");
                                continue;
                            }

                            string? doi = entry["prism:doi"]?.ToString();
                            string? potentialPdfUrl = string.IsNullOrEmpty(doi) ? null : $"https://doi.org/{doi}";
                            string? pdfText = string.Empty;
                            int? pageCount = null;

                            if (!string.IsNullOrEmpty(potentialPdfUrl))
                            {
                                try
                                {
                                    potentialPdfUrl = await Helper.GetPdfUrlFromHtmlAsync(potentialPdfUrl);
                                    if (!string.IsNullOrEmpty(potentialPdfUrl) && downloadPdfToCountPagesOf)
                                    {
                                        byte[]? pdf = await Helper.DownloadPdfFromUrlAsync(potentialPdfUrl);
                                        if (pdf != null)
                                        {
                                            pageCount = Helper.CountPdfPages(pdf);
                                            pdfText = Helper.ExtractPdfText(pdf);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("Scopus: failed to download PDF from: {Url}", potentialPdfUrl);
                                            potentialPdfUrl = string.Empty;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("Scopus: error downloading PDF → {Message}", ex.Message);
                                    potentialPdfUrl = string.Empty;
                                }
                            }

                            string tempIssn = entry["prism:issn"]?.ToString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(tempIssn))
                            {
                                tempIssn = Helper.NormalizeIssn(tempIssn);
                            }

                            var publication = new Publication
                            {
                                Portal = portal,
                                Title = entry["dc:title"]?.ToString() ?? string.Empty,
                                Doi = doi ?? string.Empty,
                                PdfUrl = potentialPdfUrl ?? string.Empty,
                                Issn = tempIssn,
                                PdfText = pdfText ?? string.Empty,
                                Pages = pageCount?.ToString()
                            };

                            await SetAbstractValuesById(scopusId, publication, authorCache, cancellationToken);
                            results.Add(publication);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Scopus: error processing entry.");
                        }
                    }

                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "Scopus: HTTP request error at page {Page}", page);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scopus: unexpected error at page {Page}", page);
                    break;
                }
                finally
                {
                    page++;
                    processedCount += pageSize;
                    await Task.Delay(RateLimit, cancellationToken);
                }
            }
            while (processedCount < totalPublications && page <= pages && !cancellationToken.IsCancellationRequested);

            _logger.LogInformation("Scopus: search completed. Publications found: {Count}", results.Count);
            return results;
        }

        private async Task SetAbstractValuesById(string scopusId, Publication publication, Dictionary<string, Author> authorCache, CancellationToken cancellationToken = default)
        {
            using var httpClient = _clientFactory.CreateClient("Scopus");
            var requestUrl = $"{httpClient.BaseAddress}abstract/scopus_id/{scopusId}";

            try
            {
                var response = await httpClient.GetAsync(requestUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Scopus: failed to retrieve abstract for ID {Id}. Status: {Status}", scopusId, response.StatusCode);
                    return;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var responseObject = JObject.Parse(jsonResponse);

                var coreData = responseObject["abstracts-retrieval-response"]?["coredata"];
                var authors = responseObject["abstracts-retrieval-response"]?["authors"]?["author"] as JArray;

                publication.Abstract = coreData?["dc:description"]?.ToString() ?? string.Empty;

                var coverDate = coreData?["prism:coverDate"]?.ToString();
                if (!string.IsNullOrEmpty(coverDate) && DateTime.TryParse(coverDate, out var parsedDate))
                {
                    publication.PublicationYear = parsedDate.Year;
                    publication.PublicationMonth = parsedDate.Month;
                    publication.PublicationDay = parsedDate.Day;
                }

                var publicationAuthors = new List<PublicationAuthor>();
                if (authors != null)
                {
                    foreach (var authorNode in authors)
                    {
                        var fullName = authorNode["preferred-name"]?["ce:indexed-name"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(fullName)) continue;

                        if (!authorCache.TryGetValue(fullName, out var author))
                        {
                            author = new Author { Name = fullName };
                            authorCache[fullName] = author;
                        }

                        publicationAuthors.Add(new PublicationAuthor { Author = author });
                    }
                }

                publication.PublicationsAuthors = publicationAuthors;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scopus: error retrieving metadata for ID: {Id}", scopusId);
            }
            finally
            {
                await Task.Delay(RateLimit, cancellationToken);
            }
        }
    }
}
