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
        /// Remove fundo verde chroma-key (#00FF00) da imagem.
        /// Estratégia: o prompt instrui a IA a usar fundo verde brilhante.
        /// Qualquer pixel onde verde >> vermelho e verde >> azul é fundo.
        /// Muito mais confiável que detecção de borda.
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
                
                int totalPixels = w * h;
                int removedCount = 0;
                
                for (int i = 0; i < totalPixels; i++)
                {
                    int p = i * 4;
                    int r = rgba[p];
                    int g = rgba[p + 1];
                    int b = rgba[p + 2];
                    
                    // Chroma key green detection:
                    // Green must be significantly higher than both red and blue
                    // AND green must be reasonably bright (not dark green from clothes/hair)
                    bool isGreenBg = (g > r + 40) && (g > b + 40) && (g > 100);
                    
                    if (isGreenBg)
                    {
                        rgba[p + 3] = 0; // alpha = 0 (transparent)
                        removedCount++;
                    }
                }
                
                // Safety: if <2% or >40% removed, skip (probably no green bg or eating subject)
                float removedPct = (float)removedCount / totalPixels;
                if (removedPct < 0.02f || removedPct > 0.40f)
                    return;
                
                // Anti-aliasing edge cleanup: for pixels at the boundary,
                // reduce alpha proportionally to how "green" they are
                for (int i = 0; i < totalPixels; i++)
                {
                    int p = i * 4;
                    if (rgba[p + 3] == 0) continue; // already transparent
                    
                    int r = rgba[p];
                    int g = rgba[p + 1];
                    int b = rgba[p + 2];
                    
                    // Partial green: anti-aliased edge pixel
                    if (g > r + 20 && g > b + 20 && g > 80)
                    {
                        // Calculate how "green" this pixel is (0-255)
                        int greenness = g - (r + b) / 2;
                        if (greenness < 0) greenness = 0;
                        if (greenness > 255) greenness = 255;
                        
                        // Reduce alpha based on greenness
                        int newAlpha = 255 - greenness;
                        if (newAlpha < 0) newAlpha = 0;
                        rgba[p + 3] = (byte)newAlpha;
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
