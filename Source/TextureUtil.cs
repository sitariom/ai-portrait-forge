using System;
using UnityEngine;
using Verse;

namespace Avatar
{
    public static class TextureUtil
    {
        public static void ClearTexture(Texture2D texture, Color color)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture canvas = RenderTexture.GetTemporary(texture.width, texture.height);
            RenderTexture.active = canvas;
            GL.Clear(true, true, color);
            texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            RenderTexture.ReleaseTemporary(canvas);
            RenderTexture.active = active;
        }
        public static Texture2D MakeReadableCopy(Texture texture, int? targetWidth = null, int? targetHeight = null)
        {
            int width = targetWidth ?? texture.width;
            int height = targetHeight ?? texture.height;
            RenderTexture active = RenderTexture.active;
            RenderTexture canvas = RenderTexture.GetTemporary(width, height);
            Texture2D result = new (width, height);
            Graphics.Blit(texture, canvas);
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.ReleaseTemporary(canvas);
            RenderTexture.active = active;
            return result;
        }
        public static Texture2D ScaleX2(Texture2D texture)
        {
            Texture2D result = new (2*texture.width, 2*texture.height);
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    if (x > 0 && y > 0 && texture.GetPixel(x-1,y) == texture.GetPixel(x,y-1) && texture.GetPixel(x,y) != texture.GetPixel(x-1,y-1))
                        result.SetPixel(2*x, 2*y, texture.GetPixel(x,y-1));
                    else
                        result.SetPixel(2*x, 2*y, texture.GetPixel(x,y));
                    if (x < texture.width-1 && y > 0 && texture.GetPixel(x+1,y) == texture.GetPixel(x,y-1) && texture.GetPixel(x,y) != texture.GetPixel(x+1,y-1))
                        result.SetPixel(2*x+1, 2*y, texture.GetPixel(x,y-1));
                    else
                        result.SetPixel(2*x+1, 2*y, texture.GetPixel(x,y));
                    if (x > 0 && y < texture.height-1 && texture.GetPixel(x-1,y) == texture.GetPixel(x,y+1) && texture.GetPixel(x,y) != texture.GetPixel(x-1,y+1))
                        result.SetPixel(2*x, 2*y+1, texture.GetPixel(x,y+1));
                    else
                        result.SetPixel(2*x, 2*y+1, texture.GetPixel(x,y));
                    if (x < texture.width-1 && y < texture.height-1 && texture.GetPixel(x+1,y) == texture.GetPixel(x,y+1) && texture.GetPixel(x,y) != texture.GetPixel(x+1,y+1))
                        result.SetPixel(2*x+1, 2*y+1, texture.GetPixel(x,y+1));
                    else
                        result.SetPixel(2*x+1, 2*y+1, texture.GetPixel(x,y));
                }
            }
            result.Apply();
            return result;
        }
        public static Texture2D ProcessVanillaTexture(VanillaTexOption opt, (int, int) size, (int, int) scale)
        // creates a copy of the texture, needs to be freed!
        {
            if (!AvatarMod.cachedTextures.ContainsKey(opt.texPath))
            {
                Texture2D raw = ContentFinder<Texture2D>.Get(opt.texPath);
                Texture2D resized = MakeReadableCopy(raw, scale.Item1, scale.Item2);
                Texture2D result = new (size.Item1, size.Item2);
                int xOffset = (scale.Item1-size.Item1)/2;

                Texture2D grayPalette = LoadedModManager.GetMod<AvatarMod>().GetTexture("gray");
                for (int y = 0; y < result.height; y++)
                {
                    // very stupid coordinate change
                    int yCoord = ((opt.rescale && y < result.height/2) ? (y/2 + result.height/4) : y) + opt.offset;
                    for (int x = 0; x < result.width; x++)
                    {
                        Color old = resized.GetPixel(x+xOffset,yCoord);
                        if (old.a < 0.5)
                            result.SetPixel(x,y,new Color(0,0,0,0));
                        else {
                            float gray = (old.r+old.g+old.b)/3f;
                            switch (opt.recolor)
                            {
                                case RecolorOption.Yes:
                                    if (gray > 0.9)
                                        result.SetPixel(x,y,grayPalette.GetPixel(0,0));
                                    else if (gray > 0.3)
                                        result.SetPixel(x,y,grayPalette.GetPixel(1,0));
                                    else
                                        result.SetPixel(x,y,grayPalette.GetPixel(2,0));
                                    break;
                                case RecolorOption.Gray:
                                    gray = ((float)Math.Round(gray*5f)+2f)/7f;
                                    result.SetPixel(x,y,new Color(gray, gray, gray, 1f));
                                    break;
                                case RecolorOption.No:
                                    result.SetPixel(x,y,old);
                                    break;
                            }
                        }
                    }
                }
                result.Apply();
                AvatarMod.cachedTextures[opt.texPath] = result;
                UnityEngine.Object.Destroy(resized);
            }
            return MakeReadableCopy(AvatarMod.cachedTextures[opt.texPath]);
        }
        public static void SavePng(string path, Texture2D texture)
        {
            if (texture.isReadable)
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            else
            {
                Texture2D copy = MakeReadableCopy(texture);
                System.IO.File.WriteAllBytes(path, copy.EncodeToPNG());
                UnityEngine.Object.Destroy(copy);
            }
        }

        /// <summary>
        /// Remove o fundo de uma imagem PNG já salva em disco.
        /// Opera diretamente nos bytes do arquivo — NÃO usa Unity APIs
        /// e pode ser chamado de qualquer thread.
        /// </summary>
        public static void RemoveBackground(string path)
        {
            try
            {
                // Read RGBA32 pixels directly from PNG bytes
                byte[] rgba;
                int w, h;
                if (!DecodePngToRGBA(path, out rgba, out w, out h))
                    return;
                
                int totalPixels = w * h;
                
                // Sample ALL edge pixels to find background color range
                int edgePixels = w * 2 + (h - 2) * 2;
                float minR = 255, minG = 255, minB = 255;
                float maxR = 0, maxG = 0, maxB = 0;
                
                for (int x = 0; x < w; x++)
                {
                    SampleEdge(rgba, x, 0, w, ref minR, ref minG, ref minB, ref maxR, ref maxG, ref maxB);
                    SampleEdge(rgba, x, h - 1, w, ref minR, ref minG, ref minB, ref maxR, ref maxG, ref maxB);
                }
                for (int y = 1; y < h - 1; y++)
                {
                    SampleEdge(rgba, 0, y, w, ref minR, ref minG, ref minB, ref maxR, ref maxG, ref maxB);
                    SampleEdge(rgba, w - 1, y, w, ref minR, ref minG, ref minB, ref maxR, ref maxG, ref maxB);
                }
                
                float bgR = (minR + maxR) / 2f;
                float bgG = (minG + maxG) / 2f;
                float bgB = (minB + maxB) / 2f;
                
                // Dynamic tolerance: larger of: (range * 1.5) or 40
                float range = (maxR - minR + maxG - minG + maxB - minB) / 3f;
                float tolerance = range * 1.2f;
                if (tolerance < 25f) tolerance = 25f;
                if (tolerance > 70f) tolerance = 70f;
                
                // Edge-flood: mark edge-connected background pixels
                bool[] visited = new bool[totalPixels];
                bool[] isBg = new bool[totalPixels];
                var queue = new System.Collections.Generic.Queue<int>();
                
                for (int x = 0; x < w; x++)
                {
                    TrySeed(rgba, visited, isBg, queue, x, 0, w, bgR, bgG, bgB, tolerance);
                    TrySeed(rgba, visited, isBg, queue, x, h - 1, w, bgR, bgG, bgB, tolerance);
                }
                for (int y = 1; y < h - 1; y++)
                {
                    TrySeed(rgba, visited, isBg, queue, 0, y, w, bgR, bgG, bgB, tolerance);
                    TrySeed(rgba, visited, isBg, queue, w - 1, y, w, bgR, bgG, bgB, tolerance);
                }
                
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int x = idx % w;
                    int y = idx / w;
                    TryNeighbor(rgba, visited, isBg, queue, x - 1, y, w, h, bgR, bgG, bgB, tolerance);
                    TryNeighbor(rgba, visited, isBg, queue, x + 1, y, w, h, bgR, bgG, bgB, tolerance);
                    TryNeighbor(rgba, visited, isBg, queue, x, y - 1, w, h, bgR, bgG, bgB, tolerance);
                    TryNeighbor(rgba, visited, isBg, queue, x, y + 1, w, h, bgR, bgG, bgB, tolerance);
                }
                
                // Safety: if >50% of image would become transparent, skip (subject being eaten)
                int bgCount = 0;
                for (int i = 0; i < totalPixels; i++)
                    if (isBg[i]) bgCount++;
                
                if (bgCount > totalPixels * 0.55f)
                    return; // Too much removed — likely eating the subject
                
                // Apply transparency to background pixels
                for (int i = 0; i < totalPixels; i++)
                {
                    if (isBg[i])
                        rgba[i * 4 + 3] = 0;
                }
                
                // Hole fill: any transparent pixel completely surrounded by opaque should be made opaque
                FillEnclosedHoles(rgba, w, h, totalPixels);
                
                // Re-encode as PNG
                byte[] pngOut = EncodeRGBAtoPNG(rgba, w, h);
                System.IO.File.WriteAllBytes(path, pngOut);
            }
            catch (System.Exception e)
            {
                Verse.Log.Warning("Avatar: Background removal failed: " + e.Message);
            }
        }
        
        private static void SampleEdge(byte[] rgba, int x, int y, int w,
            ref float minR, ref float minG, ref float minB,
            ref float maxR, ref float maxG, ref float maxB)
        {
            int i = (y * w + x) * 4;
            float r = rgba[i], g = rgba[i+1], b = rgba[i+2];
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
        }
        
        private static void FillEnclosedHoles(byte[] rgba, int w, int h, int totalPixels)
        {
            // Build a mask: 255 = "reachable from edge" (background), 0 = "enclosed" (hole or subject)
            byte[] mask = new byte[totalPixels];
            var q = new System.Collections.Generic.Queue<int>();
            
            // Seed all edge pixels
            for (int x = 0; x < w; x++)
            {
                int iTop = x * 4;
                int iBot = ((h - 1) * w + x) * 4;
                if (rgba[iTop+3] == 0) { mask[x] = 255; q.Enqueue(x); }
                if (rgba[iBot+3] == 0) { mask[(h-1)*w + x] = 255; q.Enqueue((h-1)*w + x); }
            }
            for (int y = 1; y < h - 1; y++)
            {
                int iLeft = (y * w) * 4;
                int iRight = (y * w + w - 1) * 4;
                if (rgba[iLeft+3] == 0) { mask[y*w] = 255; q.Enqueue(y*w); }
                if (rgba[iRight+3] == 0) { mask[y*w+w-1] = 255; q.Enqueue(y*w+w-1); }
            }
            
            // Flood fill to mark all edge-reachable transparent pixels
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int x = idx % w, y = idx / w;
                int[] nx = { x-1, x+1, x, x };
                int[] ny = { y, y, y-1, y+1 };
                for (int d = 0; d < 4; d++)
                {
                    int nx2 = nx[d], ny2 = ny[d];
                    if (nx2 < 0 || nx2 >= w || ny2 < 0 || ny2 >= h) continue;
                    int nidx = ny2 * w + nx2;
                    if (mask[nidx] == 0 && rgba[nidx*4+3] == 0)
                    {
                        mask[nidx] = 255;
                        q.Enqueue(nidx);
                    }
                }
            }
            
            // Any transparent pixel NOT reached by the flood = enclosed hole → make opaque
            for (int i = 0; i < totalPixels; i++)
            {
                if (rgba[i*4+3] == 0 && mask[i] == 0)
                    rgba[i*4+3] = 255;
            }
        }
        
        private static void TrySeed(byte[] rgba, bool[] visited, bool[] isBg,
            System.Collections.Generic.Queue<int> queue, int x, int y, int w,
            float bgR, float bgG, float bgB, float tol)
        {
            int idx = y * w + x;
            if (visited[idx]) return;
            visited[idx] = true;
            int i = idx * 4;
            float dr = rgba[i] - bgR; if (dr < 0) dr = -dr;
            float dg = rgba[i+1] - bgG; if (dg < 0) dg = -dg;
            float db = rgba[i+2] - bgB; if (db < 0) db = -db;
            if ((dr + dg + db) / 3f <= tol)
            {
                isBg[idx] = true;
                queue.Enqueue(idx);
            }
        }
        
        private static void TryNeighbor(byte[] rgba, bool[] visited, bool[] isBg,
            System.Collections.Generic.Queue<int> queue, int x, int y, int w, int h,
            float bgR, float bgG, float bgB, float tol)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            TrySeed(rgba, visited, isBg, queue, x, y, w, bgR, bgG, bgB, tol);
        }
        
        /// <summary>Decodes a PNG file into raw RGBA32 bytes. Pure .NET, no Unity.</summary>
        private static bool DecodePngToRGBA(string path, out byte[] rgba, out int w, out int h)
        {
            rgba = null; w = 0; h = 0;
            try
            {
                // Load PNG via System.Drawing (available on Windows .NET Framework)
                using (var bmp = System.Drawing.Image.FromFile(path))
                using (var resultBmp = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(resultBmp))
                    {
                        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    }
                    w = resultBmp.Width;
                    h = resultBmp.Height;
                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var bmpData = resultBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    rgba = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgba, 0, rgba.Length);
                    resultBmp.UnlockBits(bmpData);
                }
                return true;
            }
            catch
            {
                // System.Drawing not available or image invalid — skip
                return false;
            }
        }
        
        /// <summary>Encodes RGBA32 bytes to PNG. Pure .NET, no Unity.</summary>
        private static byte[] EncodeRGBAtoPNG(byte[] rgba, int w, int h)
        {
            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmpData.Scan0, rgba.Length);
                bmp.UnlockBits(bmpData);
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
