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
using System.Timers;

namespace OpenSim.Services.MapImageService;

public class MapImageService : IMapImageService
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[MAP IMAGE SERVICE]";
    private const int ZOOM_LEVELS = 8;
    private const int IMAGE_WIDTH = 256;
    private const int JPEG_QUALITY = 80;

    private static string m_TilesStoragePath = "maptiles";

    private static readonly object m_FileAccessLock = new();
    private static bool m_Initialized = false;
    private static readonly SKColor m_Watercolor = new(29, 72, 96);
    private static byte[] m_WaterJPEGBytes = null;

    // Return this to callers, so they can't modify m_WaterJPEGBytes.
    public static byte[] WaterJPEG
    {
        get => [.. m_WaterJPEGBytes];
    }

    public MapImageService(IConfigSource config)
    {
        lock (m_FileAccessLock)
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
            lock (m_FileAccessLock)
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
        WorkQueue.Enqueue(x, y, scopeID);
        return true;
    }

    public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
    {
        reason = string.Empty;
        string fileName = GetTileFileName(1, x, y, scopeID);

        try
        {
            lock (m_FileAccessLock)
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
        WorkQueue.Enqueue(x, y, scopeID);
        return true;
    }

    public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
    {
        string fullName = Path.Combine(GetScopeFolder(scopeID), fileName);

        if (File.Exists(fullName))
        {
            format = Path.GetExtension(fullName).ToLower();
            try
            {
                lock (m_FileAccessLock)
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
        return WaterJPEG;
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



    #region Zoom tile creation


    private sealed class WorkQueue
    {
        private static bool hasWork = false;
        private static bool triggered = false;
        private static bool isWorking = false;
        private static Dictionary<UUID, HashSet<TileInfo>> pendingWork = [];
        private static Dictionary<UUID, HashSet<TileInfo>> currentWork = [];
        private static System.Timers.Timer timer = null;
        private static readonly Object m_queueLock = new();
        private readonly record struct TileInfo
        {
            public readonly int x;
            public readonly int y;
            public TileInfo(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        public static void Enqueue(int x, int y, UUID scopeID)
        {
            lock (m_queueLock)
            {
                HashSet<TileInfo> tiles;
                if (pendingWork.TryGetValue(scopeID, out tiles))
                {
                    tiles = [];
                    pendingWork[scopeID] = tiles;
                }

                tiles.Add(new TileInfo(x, y));
                hasWork = true;

                if (timer is null)
                {
                    timer = new(5000) { AutoReset = false };
                    timer.Elapsed += TriggerFired;
                }
                timer.Start();
            }
        }

        private static void TriggerFired(Object o, ElapsedEventArgs e)
        {
            bool shouldFire = false;
            lock (m_queueLock)
            {
                triggered = hasWork;
                shouldFire = hasWork && (!isWorking);
            }

            if (shouldFire)
            {
                Util.FireAndForget(ZoomsWorker);
            }
        }

        private static void ZoomsWorker(Object o)
        {
            bool shouldContinue = false;
            do
            {
                lock (m_queueLock)
                {
                    (currentWork, pendingWork) = (pendingWork, currentWork);
                    pendingWork.Clear();
                    hasWork = false;
                    isWorking = true;
                }
                shouldContinue = DoScopes();
            } while (shouldContinue);
        }

        private static bool DoScopes()
        {
            foreach (var key in currentWork.Keys)
            {
                DoZooms(currentWork[key], key);
            }

            return false;
        }

        private static void DoZooms(HashSet<TileInfo> levelOneWorkSet, UUID scopeID)
        {
            string scopePath = GetScopeFolder(scopeID);

            HashSet<TileInfo> childSet = levelOneWorkSet;
            HashSet<TileInfo> parentSet;

            for (int childLevel = 1; childLevel < ZOOM_LEVELS; childLevel++)
            {
                parentSet = [];

                while (childSet.Count != 0)
                {
                    TileInfo childTile = childSet.GetEnumerator().Current;
                    uint parentSize = 1u << (childLevel - 1);
                    uint mask = ~((parentSize << 1) - 1u);
                    int px = (int)(((uint)childTile.x) & mask);
                    int py = (int)(((uint)childTile.y) & mask);
                    int stride = (int)parentSize;

                    using SKBitmap bottomLeft = GetExistingTileImage(childLevel, px, py, scopePath);
                    using SKBitmap bottomRight = GetExistingTileImage(childLevel, px + stride, py, scopePath);
                    using SKBitmap topLeft = GetExistingTileImage(childLevel, px, py + stride, scopePath);
                    using SKBitmap topRight = GetExistingTileImage(childLevel, px + stride, py + stride, scopePath);

                    childSet.Remove(new TileInfo(px, py));
                    childSet.Remove(new TileInfo(px + stride, py));
                    childSet.Remove(new TileInfo(px, py + stride));
                    childSet.Remove(new TileInfo(px + stride, py + stride));

                    parentSet.Add(new TileInfo(px, py));

                    string parentFile = GetTileFileName(childLevel + 1, px, py, scopePath);

                    bool regenerate = bottomLeft is not null || bottomRight is not null || topLeft is not null || topRight is not null;

                    if (regenerate)
                    {
                        using SKBitmap tempBitmap = SkiaImageUtils.NewDefaultSKBitmap(512, 512);
                        using SKCanvas tempCanvas = new(tempBitmap);
                        tempCanvas.Clear(m_Watercolor);

                        if (bottomLeft is not null) tempCanvas.DrawBitmap(bottomLeft, 0, IMAGE_WIDTH);
                        if (bottomRight is not null) tempCanvas.DrawBitmap(bottomRight, IMAGE_WIDTH, IMAGE_WIDTH);
                        if (topLeft is not null) tempCanvas.DrawBitmap(topLeft, 0, 0);
                        if (topRight is not null) tempCanvas.DrawBitmap(topRight, IMAGE_WIDTH, 0);

                        using SKBitmap newTile = tempBitmap.Resize(new SKImageInfo(IMAGE_WIDTH, IMAGE_WIDTH, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                        using SKData newTileData = newTile.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

                        try
                        {
                            lock (m_FileAccessLock)
                            {
                                using FileStream fs = File.Create(parentFile);
                                newTileData.SaveTo(fs);
                            }
                        }
                        catch (Exception e)
                        {
                            m_log.Warn($"{LogHeader}: Unable to save new zoom map tile {parentFile}. Reason: {e.Message}");
                        }
                    }
                    else
                    {
                        if (File.Exists(parentFile))
                        {
                            try
                            {
                                lock (m_FileAccessLock)
                                {
                                    File.Delete(parentFile);
                                }
                            }
                            catch (Exception e)
                            {
                                m_log.Warn($"{LogHeader}: Unable to delete all-water tile {parentFile}. Reason: {e.Message}");
                            }
                        }
                    }
                }

                childSet = parentSet;
            }
        }

        private static SKBitmap GetExistingTileImage(int level, int x, int y, string path)
        {
            string fileName = GetTileFileName(level, x, y, path);

            if (File.Exists(fileName))
            {
                try
                {
                    lock (m_FileAccessLock)
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

    }
}



#endregion







