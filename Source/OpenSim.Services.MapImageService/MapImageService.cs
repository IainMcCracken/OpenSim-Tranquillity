/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * The design of this map service is based on SimianGrid's PHP-based
 * map service. See this URL for the original PHP version:
 * https://github.com/openmetaversefoundation/simiangrid/
 */

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using System.Reflection;
using SkiaSharp;

namespace OpenSim.Services.MapImageService;

public class MapImageService : IMapImageService
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private readonly string LogHeader = "[MAP IMAGE SERVICE]";
    private const int ZOOM_LEVELS = 8;
    private const int IMAGE_WIDTH = 256;
    private const int JPEG_QUALITY = 80;

    private static string m_TilesStoragePath = "maptiles";

    private static readonly object m_Sync = new();
    private static bool m_Initialized = false;
    private static readonly SKColor m_Watercolor = new(29, 72, 96);
    private static byte[] m_WaterJPEGBytes = null;

    public MapImageService(IConfigSource config)
    {
        lock (m_Sync)
        {
            if (!m_Initialized)
            {
                using var tempWaterBitmap = new SKBitmap(IMAGE_WIDTH, IMAGE_WIDTH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                tempWaterBitmap.Erase(m_Watercolor);

                using var tempWaterData = tempWaterBitmap.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);
                m_WaterJPEGBytes = tempWaterData.ToArray();

                IConfig serviceConfig = config.Configs["MapImageService"];
                if (serviceConfig is not null)
                {
                    m_TilesStoragePath = serviceConfig.GetString("TilesStoragePath", m_TilesStoragePath);
                }

                m_Initialized = true;
            }
        }
    }

    #region Module API

    public bool AddMapTile(int x, int y, byte[] imageData, UUID scopeID, out string reason)
    {
        reason = string.Empty;

        SKBitmap inputImage;
        byte[] jpegBytes;

        // Don't trust unknown bytes from the Internet. You don't know where they've been!
        // First, are they a valid image? We'll take anything SkiaSharp can decode, or a JPEG2000.
        if (!SkiaImageUtils.TryDecodeFromBytes(imageData, out inputImage) && !SkiaImageUtils.TryDecodeFromJ2K(imageData, out inputImage))
        {
            reason = $"The submitted data is not an image file";
            m_log.Warn($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
            return false;
        }

        // Note SKBitmaps hold unmanaged data, so must be disposed of properly.
        using (inputImage)
        {
            // Ok, the image is valid. Is it the right size? It has to be 256x256.
            if (inputImage.Width != IMAGE_WIDTH || inputImage.Height != IMAGE_WIDTH)
            {
                reason = $"The image is not 256x256. It is {inputImage.Width}x{inputImage.Height}";
                m_log.Warn($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
                return false;
            }

            // And does it cleanly encode back to regular JPEG? Note, we also normalize all level-1 tiles to the same JPEG quality.
            if (!SkiaImageUtils.TryEncodeToJpeg(inputImage, JPEG_QUALITY, out jpegBytes))
            {
                reason = $"Failed to re-encode the submitted data as JPEG.";
                m_log.Warn($"{LogHeader}: Add map tile at {x},{y} failed: {reason}");
                return false;
            }
        }

        // We have a valid byte[] with a normalized JPEG in it. Now we can write it to disk.
        string fileName = GetTileFileName(1, x, y, scopeID);
        try
        {
            lock (m_Sync)
            {
                CreateScopeFolder(scopeID);
                File.WriteAllBytes(fileName, jpegBytes);
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            m_log.Warn($"{LogHeader}: Unable to save incoming image to {fileName}. Message: {reason}");
            return false;
        }

        // If the write succeeded, we can queue this tile up for producing the relevant zoomed map tiles.
        return UpdateMultiResolutionFiles(x, y, scopeID);
    }

    public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
    {
        reason = string.Empty;
        string fileName = GetTileFileName(1, x, y, scopeID);

        try
        {
            lock (m_Sync)
            {
                File.Delete(fileName);
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            m_log.Warn($"{LogHeader}: Unable to delete file {fileName}. Reason: {reason}");
            return false;
        }

        // Queue up the deletion for regenerating zoomed map tiles.
        return UpdateMultiResolutionFiles(x, y, scopeID);
    }

    public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
    {
        string fullName = Path.Combine(GetScopeFolder(scopeID), fileName);

        if (File.Exists(fullName))
        {
            format = Path.GetExtension(fullName).ToLower();
            try
            {
                lock (m_Sync)
                {
                    if (IsMaptileJpeg(fullName))
                    {
                        using var fs = File.OpenRead(fullName);
                        using var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                // Intentionally left blank.
                // The code in the try clause should not throw anything, as the server is known to have access to the file when
                // File.Exists() returns true. But, if the file read fails for any reason, we need to drop down below and return an
                // ocean tile.
            }
        }

        // The file either didn't exist, or was not what we expected, so return an empty ocean tile instead.
        format = ".jpg";
        // Make a copy, so callers cannot mutate our private field.
        return [.. m_WaterJPEGBytes];
    }

    #endregion

    #region File and filesystem Utils

    /// <summary>
    /// Get the map tiles directory for the given scope UUID
    /// </summary>
    /// <param name="scopeID"></param>
    /// <returns>the folder pathname</returns>
    private static string GetScopeFolder(UUID scopeID)
    {
        return Path.Combine(m_TilesStoragePath, scopeID.ToString());
    }

    /// <summary>
    /// Get the map tiles directory for the given scope UUID, creating it if needed
    /// </summary>
    /// <param name="scopeID"></param>
    /// <returns>the folder pathname</returns>
    private static string CreateScopeFolder(UUID scopeID)
    {
        string path = GetScopeFolder(scopeID);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Tack the filename onto the scope directory
    /// </summary>
    /// <param name="zoomLevel"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="scopeID"></param>
    /// <returns>the file full pathname</returns>
    private static string GetTileFileName(int zoomLevel, int x, int y, UUID scopeID)
    {
        return Path.Combine(GetScopeFolder(scopeID), $"map-{zoomLevel}-{x}-{y}-objects.jpg");
    }

    /// <summary>
    /// Tack the filename onto the given path
    /// </summary>
    /// <param name="zoomLevel"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="path"></param>
    /// <returns>the file full pathname</returns>
    private static string GetTileFileName(int zoomLevel, int x, int y, string path)
    {
        return Path.Combine(path, $"map-{zoomLevel}-{x}-{y}-objects.jpg");
    }

    /// <summary>
    /// Check if a given map tile file is a JPEG and is 256x256.
    /// </summary>
    /// <param name="fileName">the file</param>
    /// <returns>true if the tile is a 256x256 JPEG</returns>
    private static bool IsMaptileJpeg(string fileName)
    {
        if (File.Exists(fileName))
        {
            using var fs = File.OpenRead(fileName);
            byte[] sig = new byte[3];

            fs.Read(sig, 0, 3);
            if (SkiaImageUtils.IsNotJpeg(sig)) return false;

            // Rewind the stream for DecodeBounds
            fs.Seek(0, SeekOrigin.Begin);

            var info = SKBitmap.DecodeBounds(fs);

            return info.Width == IMAGE_WIDTH && info.Height == IMAGE_WIDTH;
        }

        return false;
    }

    #endregion

    #region Zoom Tile Generation

    private bool CreateZoomTile(int zoomLevel, int inx, int iny, string path)
    {
        int previousLevel = zoomLevel - 1;
        int prevStep = 1 << previousLevel - 1;

        int mask = unchecked((int)0xffffffff) << previousLevel;

        // Convert x and y to the bottom left of current tile
        int x = inx & mask;
        int y = iny & mask;

        bool didTiles = false;

        // A temporary 512x512 bitmap, initially cleared to sea water color.
        using var tempBitmap = new SKBitmap(IMAGE_WIDTH * 2, IMAGE_WIDTH * 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var tempCanvas = new SKCanvas(tempBitmap);
        tempCanvas.Clear(m_Watercolor);

        // Draw the four map tiles (from the next zoom level up) onto this temporary bitmap (if they exist).
        using (var bottomLeft = GetExistingTileImage(previousLevel, x, y, path))
            if (bottomLeft is not null)
            {
                tempCanvas.DrawBitmap(bottomLeft, 0, IMAGE_WIDTH);
                didTiles = true;
            }
        using (var bottomRight = GetExistingTileImage(previousLevel, x + prevStep, y, path))
            if (bottomRight is not null)
            {
                tempCanvas.DrawBitmap(bottomRight, IMAGE_WIDTH, IMAGE_WIDTH);
                didTiles = true;
            }
        using (var topLeft = GetExistingTileImage(previousLevel, x, y + prevStep, path))
            if (topLeft is not null)
            {
                tempCanvas.DrawBitmap(topLeft, 0, 0);
                didTiles = true;
            }
        using (var topRight = GetExistingTileImage(previousLevel, x + prevStep, y + prevStep, path))
            if (topRight is not null)
            {
                tempCanvas.DrawBitmap(topRight, IMAGE_WIDTH, 0);
                didTiles = true;
            }

        string outputFile = GetTileFileName(zoomLevel, x, y, path);

        if (didTiles)
        {
            // There were tiles one zoom level up. Now resize our temp 512x512 down to 256x256 with bilinear interpolation.
            using var newTileBitmap = tempBitmap.Resize(new SKImageInfo(IMAGE_WIDTH, IMAGE_WIDTH, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            using var newTileEncoded = newTileBitmap.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

            try
            {
                lock (m_Sync)
                {
                    using var fs = File.Create(outputFile);
                    newTileEncoded.SaveTo(fs);
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader}: Unable to save new zoom map tile {outputFile}. Reason: {e.Message}");
                return false;
            }
        }
        else
        {
            // This can happen as a result of RemoveMapTile.
            try
            {
                lock (m_Sync)
                    File.Delete(outputFile);
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader}: Failed to delete unneeded zoom map tile {outputFile}. Reason: {e.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Get the image for a zoom level and grid map position.
    /// </summary>
    /// <param name="zoomlevel"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="path"></param>
    /// <returns>a Skia bitmap, or null if the file does not exist</returns>
    private SKBitmap GetExistingTileImage(int zoomlevel, int x, int y, string path)
    {
        string fileName = GetTileFileName(zoomlevel, x, y, path);

        if (File.Exists(fileName))
        {
            try
            {
                lock (m_Sync)
                {
                    // The tiles we saved should be 256x256 JPEG files. Reject if they are not.
                    if (IsMaptileJpeg(fileName))
                    {
                        using var fs = File.OpenRead(fileName);
                        SKBitmap output = SKBitmap.Decode(fs);
                        if (output is null)
                        {
                            m_log.Error($"{LogHeader}: Failed to decode map tile {fileName}");
                            return null;
                        }

                        return output;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader}: Unable to read map tile from {fileName}", e);
            }
        }

        // The file did not exist, or we couldn't access it, or it wasn't a well-formed 256x256 JPEG.
        return null;
    }

    #endregion

    #region Zoom tile fire and forget thread

    // TODO -- This code can be fixed up to be more deterministic, without arbitrary delays within the worker thread.

    // ... existing code ...

    // When large varregions start up, they can send piles of new map tiles. This causes
    //    this multi-resolution routine to be called a zillion times an causes much CPU
    //    time to be spent creating multi-resolution tiles that will be replaced when
    //    the next maptile arrives.
    private struct MapToMultiRez
    {
        public int x;
        public int y;
        public UUID scopeID;
    }

    private readonly Queue<MapToMultiRez> m_MultiRezToBuild = new Queue<MapToMultiRez>();

    private bool UpdateMultiResolutionFiles(int x, int y, UUID scopeID)
    {
        lock (m_MultiRezToBuild)
        {
            // m_log.DebugFormat("{0} UpdateMultiResolutionFilesAsync: scheduling update for <{1},{2}>", LogHeader, x, y);
            m_MultiRezToBuild.Enqueue(
                new MapToMultiRez
                {
                    x = x,
                    y = y,
                    scopeID = scopeID
                }
            );

            if (m_MultiRezToBuild.Count == 1)
                Util.FireAndForget(DoUpdateMultiResolutionFilesAsync);
        }

        return true;
    }

    private void DoUpdateMultiResolutionFilesAsync(object o)
    {
        m_log.Debug($"{LogHeader}: Thread triggered.");
        // let acumulate large region tiles
        Thread.Sleep(1000); // large regions take time to upload tiles
        // Thread.Sleep(60 * 1000); // large regions take time to upload tiles

        while (true)
        {
            MapToMultiRez toMultiRez;
            lock (m_MultiRezToBuild)
            {
                if (!m_MultiRezToBuild.TryDequeue(out toMultiRez))
                    return;
            }

            string path = CreateScopeFolder(toMultiRez.scopeID);
            for (int zoomLevel = 2; zoomLevel <= ZOOM_LEVELS; zoomLevel++)
            {
                m_log.Debug($"{LogHeader}: Create zoom tile level {zoomLevel} for {toMultiRez.x}-{toMultiRez.y}");
                if (!CreateZoomTile(zoomLevel, toMultiRez.x, toMultiRez.y, path))
                {
                    m_log.WarnFormat("[MAP IMAGE SERVICE]: Unable to create tile for {0},{1} at zoom level {1}", toMultiRez.x, toMultiRez.y, zoomLevel);
                    return;
                }
            }
            Thread.Sleep(50); // slow things a bit
        }
    }

    #endregion
}
