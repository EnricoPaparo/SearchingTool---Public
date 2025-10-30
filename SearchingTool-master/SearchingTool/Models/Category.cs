namespace SearchingTool.Models
{
    public class Category
    {
        public int Id { get; set; }
        public string CategoryName { get; set; } = string.Empty;

        public List<PublicationCategory> PublicationsCategories { get; set; } = new();
    }

}
