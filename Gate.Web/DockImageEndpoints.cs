// Read-only dock photo endpoint. Serves bytes from the dock image
// directory only; traversal and unknown names return 404. No live calls.
using Gate.Web.Services;

namespace Gate.Web;

public static class DockImageEndpoints
{
    public static void MapDockImages(this WebApplication app)
    {
        app.MapGet("/dock-images/{name}", async (
            string name, DockPhotoService photos, CancellationToken ct) =>
        {
            var bytes = await photos.ReadImageBytesAsync(name, ct).ConfigureAwait(false);
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "image/jpeg");
        });
    }
}
