namespace SearchingTool.Models
{
    public class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public List<PublicationAuthor> PublicationsAuthors { get; set; } = new();
    }


}
