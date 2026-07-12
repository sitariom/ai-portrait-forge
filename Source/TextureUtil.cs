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
                                    if (grayPalette != null)
                                    {
                                    if (gray > 0.9)
                                        result.SetPixel(x,y,grayPalette.GetPixel(0,0));
                                    else if (gray > 0.3)
                                        result.SetPixel(x,y,grayPalette.GetPixel(1,0));
                                    else
                                        result.SetPixel(x,y,grayPalette.GetPixel(2,0));
                                    }
                                    else
                                        result.SetPixel(x,y,new Color(gray, gray, gray, 1f));
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
        /// Remove fundo branco. Flood-fill das bordas removendo pixels claros.
        /// Dois passes: 195 (agressivo) depois 210 (conservador) se o primeiro falhar.
        /// Dilatação de 1px nas bordas para suavizar transição.
        /// </summary>
        public static void RemoveBackground(string path)
        {
            try
            {
                byte[] rgba;
                int w, h;
                if (!DecodePngToRGBA(path, out rgba, out w, out h))
                    return;
                if (w < 20 || h < 20) return;
                
                // Try with lower threshold first, then higher if too little removed
                if (!TryRemoveBg(rgba, w, h, 195))
                    TryRemoveBg(rgba, w, h, 210);
                
                byte[] pngOut = EncodeRGBAtoPNG(rgba, w, h);
                System.IO.File.WriteAllBytes(path, pngOut);
            }
            catch (System.Exception e)
            {
                Verse.Log.Warning("Avatar: Background removal failed: " + e.Message);
            }
        }
        
        private static bool TryRemoveBg(byte[] rgba, int w, int h, int threshold)
        {
            int total = w * h;
            
            // Compute brightness (max of R,G,B)
            byte[] bright = new byte[total];
            for (int i = 0; i < total; i++)
            {
                int p = i * 4;
                int m = rgba[p];
                if (rgba[p+1] > m) m = rgba[p+1];
                if (rgba[p+2] > m) m = rgba[p+2];
                bright[i] = (byte)m;
            }
            
            // Flood-fill from edges
            bool[] visited = new bool[total];
            bool[] isBg = new bool[total];
            var q = new System.Collections.Generic.Queue<int>();
            
            for (int x = 0; x < w; x++)
            {
                if (bright[x] >= threshold && !visited[x])
                { visited[x] = true; isBg[x] = true; q.Enqueue(x); }
                int b = (h-1)*w + x;
                if (bright[b] >= threshold && !visited[b])
                { visited[b] = true; isBg[b] = true; q.Enqueue(b); }
            }
            for (int y = 1; y < h-1; y++)
            {
                int l = y*w, r = y*w + w-1;
                if (bright[l] >= threshold && !visited[l])
                { visited[l] = true; isBg[l] = true; q.Enqueue(l); }
                if (bright[r] >= threshold && !visited[r])
                { visited[r] = true; isBg[r] = true; q.Enqueue(r); }
            }
            
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int x = idx % w, y = idx / w;
                if (x > 0)     { int n = idx - 1; if (!visited[n] && bright[n] >= threshold) { visited[n] = true; isBg[n] = true; q.Enqueue(n); } }
                if (x < w - 1) { int n = idx + 1; if (!visited[n] && bright[n] >= threshold) { visited[n] = true; isBg[n] = true; q.Enqueue(n); } }
                if (y > 0)     { int n = idx - w; if (!visited[n] && bright[n] >= threshold) { visited[n] = true; isBg[n] = true; q.Enqueue(n); } }
                if (y < h - 1) { int n = idx + w; if (!visited[n] && bright[n] >= threshold) { visited[n] = true; isBg[n] = true; q.Enqueue(n); } }
            }
            
            int bgCount = 0;
            for (int i = 0; i < total; i++) if (isBg[i]) bgCount++;
            float pct = (float)bgCount / total;
            
            if (pct < 0.02f || pct > 0.50f) return false;
            
            // Apply: bg pixels → alpha 0
            for (int i = 0; i < total; i++)
                if (isBg[i]) rgba[i*4+3] = 0;
            
            // Dilate: 1px border around transparent → partial alpha (smooth edge)
            byte[] newAlpha = new byte[total];
            for (int i = 0; i < total; i++) newAlpha[i] = rgba[i*4+3];
            
            for (int i = 0; i < total; i++)
            {
                if (rgba[i*4+3] == 0) continue; // already transparent
                int x = i % w, y = i / w;
                // Check if any neighbor is transparent
                bool nearTransparent = false;
                if (x > 0 && rgba[(i-1)*4+3] == 0) nearTransparent = true;
                else if (x < w-1 && rgba[(i+1)*4+3] == 0) nearTransparent = true;
                else if (y > 0 && rgba[(i-w)*4+3] == 0) nearTransparent = true;
                else if (y < h-1 && rgba[(i+w)*4+3] == 0) nearTransparent = true;
                
                if (nearTransparent && bright[i] >= (threshold - 15))
                {
                    // Blend alpha based on brightness
                    int b = bright[i];
                    int a = 255 - (b - (threshold - 30)) * 8;
                    if (a < 30) a = 30;
                    if (a > 255) a = 255;
                    if (a < newAlpha[i]) newAlpha[i] = (byte)a;
                }
            }
            
            for (int i = 0; i < total; i++)
                rgba[i*4+3] = newAlpha[i];
            
            Verse.Log.Message("Avatar: BG removed " + bgCount + "/" + total + " pixels (" + pct.ToString("F1") + "%) threshold=" + threshold);
            return true;
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
