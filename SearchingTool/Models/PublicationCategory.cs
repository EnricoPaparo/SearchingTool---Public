namespace SearchingTool.Models
{
    public class PublicationCategory
    {
        public int PublicationId { get; set; }
        public Publication Publication { get; set; } = null!;

        public int CategoryId { get; set; }
        public Category Category { get; set; } = null!;

        public string Quartile { get; set; } = string.Empty;
    }

}
