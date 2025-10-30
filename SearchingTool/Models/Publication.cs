using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SearchingTool.Models
{
    public class Publication
    {
        public int Id { get; set; }

        public string? Doi { get; set; }
        public string? Title { get; set; }
        public string? Abstract { get; set; }
        public int? PublicationYear { get; set; }
        public int? PublicationMonth { get; set; }
        public int? PublicationDay { get; set; }
        public string? Issn { get; set; }
        public string? PdfUrl { get; set; }
        public string? PdfText { get; set; }
        public string? Pages { get; set; }

        public int PortalId { get; set; }
        public Portal Portal { get; set; } = null!;

        public List<Result> Results { get; set; } = new();
        public List<PublicationAuthor> PublicationsAuthors { get; set; } = new();
        public List<PublicationCategory> PublicationsCategories { get; set; } = new();
    }
}

