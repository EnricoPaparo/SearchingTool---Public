namespace SearchingTool.Models
{
    public class PublicationAuthor
    {
        public int PublicationId { get; set; }
        public Publication Publication { get; set; } = null!;

        public int AuthorId { get; set; }
        public Author Author { get; set; } = null!;
    }

}
