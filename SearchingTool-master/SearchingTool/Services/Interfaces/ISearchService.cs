using SearchingTool.Models;
using System.Threading;

namespace SearchingTool.Services.Interfaces
{
    public interface ISearchService
    {
        string SourceName { get; }

        Task<List<Publication>> SearchAsync(
            string query,
            int startYear,
            bool downloadPdf,
            int batchSize,
            int pageNumber = 1,
            CancellationToken token = default);
    }
}
