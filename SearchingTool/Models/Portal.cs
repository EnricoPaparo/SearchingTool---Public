namespace SearchingTool.Models
{
    public class Portal
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;

        public List<Publication> Publications { get; set; } = new();
    }

}
