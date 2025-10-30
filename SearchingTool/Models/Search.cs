using System.ComponentModel.DataAnnotations;

namespace SearchingTool.Models
{
    public class Search
    {
        [Key]
        public int Id { get; set; }

        public DateTime SearchDate { get; set; }

        public string QueryText { get; set; } = string.Empty;

        public int? StartYear { get; set; }

        public List<Result> Results { get; set; } = new();
    }
}
