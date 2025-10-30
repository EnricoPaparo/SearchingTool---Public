namespace SearchingTool.Models
{
    public class Journal
    {
        public string? Issn { get; set; }
        public List<CategoryWithQuartile> Categories { get; set; } = new();
        public int? Year { get; set; }
    }
}
