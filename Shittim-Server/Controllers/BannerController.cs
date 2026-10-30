using Microsoft.AspNetCore.Mvc;

namespace Shittim_Server.Controllers;

// Lobby banner art for Management_BannerList. Nothing in the pipeline serves files - the app is MapControllers plus /health - so the image needs a route.
// Catch-all because it is not settled whether the client fetches BannerDB.Url on its own or concatenates FileName onto it; both land here.
[ApiController]
public class BannerController : ControllerBase
{
    [HttpGet("/banner/{**path}")]
    public IResult Image(string path)
    {
        // GetLocalizeBannerImageName splices the client's language in ahead of the extension, so one file on disk answers for every locale
        var art = ResolveArt(Path.Combine(AppContext.BaseDirectory, "Data", "Banner"), path);

        var file = art ?? Path.Combine(AppContext.BaseDirectory, "Data", "Koyuki", "koyuki.png");
        if (!System.IO.File.Exists(file))
            return Results.NotFound();

        return Results.Bytes(System.IO.File.ReadAllBytes(file), "image/png");
    }

    internal static string? ResolveArt(string directory, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return null;
        var exact = Path.Combine(directory, name + ".png");
        if (System.IO.File.Exists(exact)) return exact;
        var cut = name.LastIndexOf('_');
        if (cut <= 0 || !new[] { "en", "kr", "ko", "jp", "ja", "tw", "th", "zh" }
                .Contains(name[(cut + 1)..], StringComparer.OrdinalIgnoreCase))
            return null;
        var localized = Path.Combine(directory, name[..cut] + ".png");
        return System.IO.File.Exists(localized) ? localized : null;
    }
}
