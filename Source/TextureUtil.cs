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
                byte[] rgba;
                int w, h;
                if (!DecodePngToRGBA(path, out rgba, out w, out h))
                    return;
                
                if (w < 20 || h < 20) return; // Too small
                
                int totalPixels = w * h;
                
                // Sample ONLY the 4 extreme corners (5x5 pixel area each, inset 2px from edge)
                int sampleSize = 5;
                float[] corners = new float[12]; // R,G,B for 4 corners
                SampleCorner(rgba, w, h, 2, 2, sampleSize, out corners[0], out corners[1], out corners[2]);
                SampleCorner(rgba, w, h, w - 2 - sampleSize, 2, sampleSize, out corners[3], out corners[4], out corners[5]);
                SampleCorner(rgba, w, h, 2, h - 2 - sampleSize, sampleSize, out corners[6], out corners[7], out corners[8]);
                SampleCorner(rgba, w, h, w - 2 - sampleSize, h - 2 - sampleSize, sampleSize, out corners[9], out corners[10], out corners[11]);
                
                // Check if all 4 corners are similar (uniform background)
                float avgR = (corners[0]+corners[3]+corners[6]+corners[9]) / 4f;
                float avgG = (corners[1]+corners[4]+corners[7]+corners[10]) / 4f;
                float avgB = (corners[2]+corners[5]+corners[8]+corners[11]) / 4f;
                
                float maxDevR = MaxDev(corners[0], corners[3], corners[6], corners[9], avgR);
                float maxDevG = MaxDev(corners[1], corners[4], corners[7], corners[10], avgG);
                float maxDevB = MaxDev(corners[2], corners[5], corners[8], corners[11], avgB);
                
                // If corners are too different, background is not uniform — skip
                if (maxDevR > 40f || maxDevG > 40f || maxDevB > 40f)
                    return;
                
                // Fixed conservative tolerance
                float tolerance = 20f;
                
                bool[] visited = new bool[totalPixels];
                bool[] isBg = new bool[totalPixels];
                var queue = new System.Collections.Generic.Queue<int>();
                
                // Seed ONLY from the 4 corners (not all edges)
                TrySeed(rgba, visited, isBg, queue, 2, 2, w, avgR, avgG, avgB, tolerance);
                TrySeed(rgba, visited, isBg, queue, w - 3, 2, w, avgR, avgG, avgB, tolerance);
                TrySeed(rgba, visited, isBg, queue, 2, h - 3, w, avgR, avgG, avgB, tolerance);
                TrySeed(rgba, visited, isBg, queue, w - 3, h - 3, w, avgR, avgG, avgB, tolerance);
                
                // Flood fill (4-directional only — no diagonals)
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int x = idx % w;
                    int y = idx / w;
                    if (x > 0) TrySeed(rgba, visited, isBg, queue, x - 1, y, w, avgR, avgG, avgB, tolerance);
                    if (x < w - 1) TrySeed(rgba, visited, isBg, queue, x + 1, y, w, avgR, avgG, avgB, tolerance);
                    if (y > 0) TrySeed(rgba, visited, isBg, queue, x, y - 1, w, avgR, avgG, avgB, tolerance);
                    if (y < h - 1) TrySeed(rgba, visited, isBg, queue, x, y + 1, w, avgR, avgG, avgB, tolerance);
                }
                
                // Safety: if >30% would become transparent, skip
                int bgCount = 0;
                for (int i = 0; i < totalPixels; i++)
                    if (isBg[i]) bgCount++;
                
                if (bgCount > totalPixels * 0.30f || bgCount < totalPixels * 0.02f)
                    return;
                
                // Apply transparency
                for (int i = 0; i < totalPixels; i++)
                {
                    if (isBg[i])
                        rgba[i * 4 + 3] = 0;
                }
                
                // Re-encode
                byte[] pngOut = EncodeRGBAtoPNG(rgba, w, h);
                System.IO.File.WriteAllBytes(path, pngOut);
            }
            catch (System.Exception e)
            {
                Verse.Log.Warning("Avatar: Background removal failed: " + e.Message);
            }
        }
        
        private static void SampleCorner(byte[] rgba, int w, int h, int startX, int startY, int size,
            out float avgR, out float avgG, out float avgB)
        {
            float r = 0, g = 0, b = 0;
            int count = 0;
            int endX = startX + size < w ? startX + size : w;
            int endY = startY + size < h ? startY + size : h;
            for (int y = startY; y < endY; y++)
                for (int x = startX; x < endX; x++)
                {
                    int i = (y * w + x) * 4;
                    r += rgba[i]; g += rgba[i+1]; b += rgba[i+2];
                    count++;
                }
            avgR = r / count;
            avgG = g / count;
            avgB = b / count;
        }
        
        private static float MaxDev(float a, float b, float c, float d, float avg)
        {
            float da = a - avg; if (da < 0) da = -da;
            float db = b - avg; if (db < 0) db = -db;
            float dc = c - avg; if (dc < 0) dc = -dc;
            float dd = d - avg; if (dd < 0) dd = -dd;
            return da > db ? (da > dc ? (da > dd ? da : dd) : (dc > dd ? dc : dd))
                 : db > dc ? (db > dd ? db : dd) : (dc > dd ? dc : dd);
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
