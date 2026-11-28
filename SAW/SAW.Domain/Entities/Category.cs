namespace SAW.Domain.Entities;

public sealed class Category
{
    public int CategoryId { get; set; }
    public int? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public Category? Parent { get; set; }
}
