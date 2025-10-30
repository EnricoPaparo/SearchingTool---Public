using System.Xml;
using Newtonsoft.Json.Linq;
using SearchingTool.Models;
using SearchingTool.Services.Interfaces;
using SearchingTool.Utils;

namespace SearchingTool.Services
{
    public class PubMedServices : ISearchService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<PubMedServices> _logger;
        private readonly PortalResolver _portalResolver;

        public string SourceName => "PubMed";

        public PubMedServices(HttpClient httpClient, IConfiguration configuration, ILogger<PubMedServices> logger, PortalResolver portalResolver)
        {
            _httpClient = httpClient;
            _logger = logger;
            _portalResolver = portalResolver;
            _apiKey = configuration["PubMedAPI:ApiKey"] ?? throw new ArgumentNullException(nameof(configuration), "Missing PubMed API key");
        }

        public async Task<List<Publication>> SearchAsync(string query, int startYear, bool downloadPdf, int batchSize, int pageNumber = 1, CancellationToken token = default)
        {
            return await SearchPubMed(query, downloadPdf, batchSize, startYear);
        }

        public async Task<List<Publication>> SearchPubMed(string query, bool downloadPdfToCountPagesOf, int batchSize, int startYear)
        {
            var results = new List<Publication>();
            var allIds = new List<string>();
            var authorCache = new Dictionary<string, Author>();
            int start = 0;

            _logger.LogInformation("PubMed: starting search with query: \"{Query}\" and start year: {Year}", query, startYear);

            try
            {
                var portal = await _portalResolver.GetPortalAsync(SourceName);
                string fullQuery = $"{query} AND eng[Language] AND {startYear}:3000[dp]";

                while (allIds.Count < batchSize)
                {
                    string searchUrl =
                        $"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esearch.fcgi?" +
                        $"db=pubmed&term={Uri.EscapeDataString(fullQuery)}" +
                        $"&retmode=json&retmax={batchSize}&retstart={start}" +
                        $"&sort=relevance" +
                        $"&api_key={_apiKey}";

                    var searchResponse = await _httpClient.GetStringAsync(searchUrl);

                    if (string.IsNullOrWhiteSpace(searchResponse))
                    {
                        _logger.LogWarning("PubMed: empty response from esearch endpoint.");
                        break;
                    }

                    var searchDoc = JObject.Parse(searchResponse);
                    var idListElement = searchDoc["esearchresult"]?["idlist"] as JArray;
                    if (idListElement == null || idListElement.Count == 0) break;

                    allIds.AddRange(idListElement.Select(id => id.ToString()));
                    start += batchSize;
                    await Task.Delay(150);
                }

                const int fetchBatchSize = 200;
                for (int i = 0; i < allIds.Count; i += fetchBatchSize)
                {
                    var batchIds = allIds.Skip(i).Take(fetchBatchSize);
                    string fetchUrl = $"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi?db=pubmed&id={string.Join(",", batchIds)}&retmode=xml&api_key={_apiKey}";
                    var fetchResponse = await _httpClient.GetStringAsync(fetchUrl);

                    if (string.IsNullOrWhiteSpace(fetchResponse)) continue;

                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(fetchResponse);
                    var articles = xmlDoc.GetElementsByTagName("PubmedArticle");

                    foreach (XmlNode article in articles)
                    {
                        try
                        {
                            string? doi = article.SelectSingleNode(".//ArticleId[@IdType='doi']")?.InnerText;
                            string? pmcid = article.SelectSingleNode(".//ArticleId[@IdType='pmc']")?.InnerText;
                            
                            string? potentialPdfUrl = pmcid != null ? $"https://www.ncbi.nlm.nih.gov/pmc/articles/{pmcid}/" : null;

                            int? pageCount = null;
                            string? pdfText = string.Empty;

                            if (!string.IsNullOrEmpty(potentialPdfUrl))
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
                                        _logger.LogWarning("PubMed: failed to download PDF from: {Url}", potentialPdfUrl);
                                        potentialPdfUrl = string.Empty;
                                    }
                                }
                            }

                            var authorNodes = article.SelectNodes(".//Author");
                            var publicationAuthors = new List<PublicationAuthor>();

                            foreach (XmlNode node in authorNodes)
                            {
                                var fullName = $"{node.SelectSingleNode("ForeName")?.InnerText ?? ""} {node.SelectSingleNode("LastName")?.InnerText ?? ""}".Trim();
                                if (string.IsNullOrWhiteSpace(fullName)) continue;

                                if (!authorCache.TryGetValue(fullName, out var author))
                                {
                                    author = new Author { Name = fullName };
                                    authorCache[fullName] = author;
                                }

                                publicationAuthors.Add(new PublicationAuthor { Author = author });
                            }

                            var pubDateNode = article.SelectSingleNode(".//PubDate");
                            int.TryParse(pubDateNode?.SelectSingleNode("Year")?.InnerText, out var year);
                            int.TryParse(pubDateNode?.SelectSingleNode("Month")?.InnerText, out var month);
                            int.TryParse(pubDateNode?.SelectSingleNode("Day")?.InnerText, out var day);

                            if (year < startYear)
                            {
                                _logger.LogDebug("PubMed: skipping publication from year {Year} (below startYear)", year);
                                continue;
                            }

                            string tempIssn = article.SelectSingleNode(".//ISSN")?.InnerText ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(tempIssn))
                            {
                                tempIssn = Helper.NormalizeIssn(tempIssn);
                            }

                            var publication = new Publication
                            {
                                Title = article.SelectSingleNode(".//ArticleTitle")?.InnerText ?? string.Empty,
                                Abstract = article.SelectSingleNode(".//AbstractText")?.InnerText ?? string.Empty,
                                Doi = doi ?? string.Empty,
                                PdfUrl = potentialPdfUrl ?? string.Empty,
                                PdfText = pdfText ?? string.Empty,
                                Issn = tempIssn,
                                Pages = pageCount?.ToString(),
                                PublicationYear = year,
                                PublicationMonth = month,
                                PublicationDay = day,
                                Portal = portal,
                                PublicationsAuthors = publicationAuthors
                            };

                            results.Add(publication);
                            await Task.Delay(150);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "PubMed: error processing article.");
                        }
                    }
                }

                _logger.LogInformation("PubMed: search completed. Publications found: {Count}", results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PubMed: fatal error during search.");
            }

            return results;
        }
    }
}
