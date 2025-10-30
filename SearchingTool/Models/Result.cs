namespace SearchingTool.Models
{
    public class Result
    {
        public int SearchId { get; set; }
        public Search Search { get; set; } = null!;

        public int PublicationId { get; set; }
        public Publication Publication { get; set; } = null!;
    }
}

