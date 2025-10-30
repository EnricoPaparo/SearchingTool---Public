using SearchingTool.Models;
using SearchingTool.Data;
using Microsoft.EntityFrameworkCore;

public class PortalResolver
{
    private readonly ScopingReviewContext _context;
    private readonly Dictionary<string, Portal> _cache = new();

    public PortalResolver(ScopingReviewContext context)
    {
        _context = context;
    }

    public async Task<Portal> GetPortalAsync(string name)
    {
        if (_cache.TryGetValue(name, out var portal))
            return portal;

        portal = await _context.Portals.FirstOrDefaultAsync(p => p.Description == name);

        if (portal == null)
        {
            portal = new Portal { Description = name };
            _context.Portals.Add(portal);
            await _context.SaveChangesAsync();
        }

        _cache[name] = portal;
        return portal;
    }
}
