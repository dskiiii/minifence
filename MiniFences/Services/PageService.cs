using MiniFences.Models;

namespace MiniFences.Services;

public static class PageService
{
    public static bool TryDeleteEmptyPage(AppConfig config, int pageIndex, out string? error)
    {
        var pageCount = GetPageCount(config);
        if (pageCount <= 1)
        {
            error = "Keep at least one page.";
            return false;
        }

        if (pageIndex < 0 || pageIndex >= pageCount)
        {
            error = "Page does not exist.";
            return false;
        }

        if (config.Fences.Any(fence => fence.PageIndex == pageIndex))
        {
            error = "Only empty pages can be deleted. Move or delete the Fences on this page first.";
            return false;
        }

        foreach (var fence in config.Fences.Where(fence => fence.PageIndex > pageIndex))
        {
            fence.PageIndex -= 1;
        }

        config.PageCount = Math.Max(1, pageCount - 1);
        config.CurrentPage = Math.Clamp(config.CurrentPage >= pageIndex ? config.CurrentPage - 1 : config.CurrentPage, 0, config.PageCount - 1);
        error = null;
        return true;
    }

    public static int GetPageCount(AppConfig config)
    {
        var maxFencePage = config.Fences.Count == 0
            ? 0
            : config.Fences.Max(fence => Math.Max(0, fence.PageIndex));
        config.PageCount = Math.Max(1, Math.Max(config.PageCount, Math.Max(maxFencePage, config.CurrentPage) + 1));
        return config.PageCount;
    }
}
