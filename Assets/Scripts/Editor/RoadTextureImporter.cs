using UnityEditor;
using UnityEngine;

namespace RocketWorld.Editor
{
    /// <summary>Import settings for the road pavement textures in Assets/Resources/Roads: *_n = normal map, *_m = data (not sRGB), the rest colour. Repeat wrap, mipmaps, anisotropic filtering (the road is seen at grazing angles).</summary>
    public sealed class RoadTextureImporter : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains("/Resources/Roads/")) return;
            var ti = (TextureImporter)assetImporter; string n = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true; ti.filterMode = FilterMode.Trilinear; ti.anisoLevel = 8; ti.maxTextureSize = 1024; ti.npotScale = TextureImporterNPOTScale.None;
            if (n.EndsWith("_n")) { ti.textureType = TextureImporterType.NormalMap; ti.textureCompression = TextureImporterCompression.Compressed; }
            else if (n.EndsWith("_m")) { ti.textureType = TextureImporterType.Default; ti.sRGBTexture = false; ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.alphaIsTransparency = false; }
            else { ti.textureType = TextureImporterType.Default; ti.sRGBTexture = true; ti.alphaSource = TextureImporterAlphaSource.None; }
        }
    }
}
