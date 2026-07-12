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
        /// Remove fundo branco da imagem. Flood-fill a partir das bordas,
        /// removendo apenas pixels muito claros (brilho > 210).
        /// O personagem nunca é afetado porque pele/roupa não chega a 210.
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
                
                int total = w * h;
                byte[] brightness = new byte[total];
                for (int i = 0; i < total; i++)
                {
                    int p = i * 4;
                    int max = rgba[p];
                    if (rgba[p+1] > max) max = rgba[p+1];
                    if (rgba[p+2] > max) max = rgba[p+2];
                    brightness[i] = (byte)max;
                }
                
                // Flood-fill from edges, only including bright pixels (>= 210)
                bool[] visited = new bool[total];
                bool[] isBg = new bool[total];
                var queue = new System.Collections.Generic.Queue<int>();
                
                // Seed all edge pixels that are bright enough
                for (int x = 0; x < w; x++)
                {
                    if (brightness[x] >= 210) { isBg[x] = true; visited[x] = true; queue.Enqueue(x); }
                    int b = (h - 1) * w + x;
                    if (brightness[b] >= 210) { isBg[b] = true; visited[b] = true; queue.Enqueue(b); }
                }
                for (int y = 1; y < h - 1; y++)
                {
                    int l = y * w;
                    if (brightness[l] >= 210) { isBg[l] = true; visited[l] = true; queue.Enqueue(l); }
                    int r = y * w + w - 1;
                    if (brightness[r] >= 210) { isBg[r] = true; visited[r] = true; queue.Enqueue(r); }
                }
                
                // Flood fill
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int x = idx % w, y = idx / w;
                    int[] nx = { x-1, x+1, x, x };
                    int[] ny = { y, y, y-1, y+1 };
                    for (int d = 0; d < 4; d++)
                    {
                        int nx2 = nx[d], ny2 = ny[d];
                        if (nx2 < 0 || nx2 >= w || ny2 < 0 || ny2 >= h) continue;
                        int nidx = ny2 * w + nx2;
                        if (!visited[nidx] && brightness[nidx] >= 210)
                        {
                            visited[nidx] = true;
                            isBg[nidx] = true;
                            queue.Enqueue(nidx);
                        }
                    }
                }
                
                // Count and safety check
                int bgCount = 0;
                for (int i = 0; i < total; i++) if (isBg[i]) bgCount++;
                float pct = (float)bgCount / total;
                if (pct < 0.03f || pct > 0.45f) return;
                
                // Apply transparency + anti-alias edge
                for (int i = 0; i < total; i++)
                {
                    int p = i * 4;
                    if (isBg[i])
                    {
                        rgba[p+3] = 0;
                    }
                    else if (brightness[i] >= 200)
                    {
                        // Edge pixel: partially transparent based on brightness
                        int alpha = 255 - (brightness[i] - 190) * 4;
                        if (alpha < 0) alpha = 0;
                        if (alpha < rgba[p+3]) rgba[p+3] = (byte)alpha;
                    }
                }
                
                byte[] pngOut = EncodeRGBAtoPNG(rgba, w, h);
                System.IO.File.WriteAllBytes(path, pngOut);
            }
            catch (System.Exception e)
            {
                Verse.Log.Warning("Avatar: Background removal failed: " + e.Message);
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
