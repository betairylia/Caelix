using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Caelix.Rendering.Native
{
    /// <summary>One render texture the plugin writes, with its native pointers.</summary>
    internal sealed class CaelixNativeTargetTexture
    {
        public RenderTexture Texture;
        public RTHandle Handle;
        public IntPtr TexturePtr;
        public IntPtr RenderBufferPtr;

        public bool Matches(int width, int height) =>
            Texture != null && Texture.width == width && Texture.height == height && Texture.IsCreated();

        public void Create(string name, GraphicsFormat format, int width, int height)
        {
            Texture = new RenderTexture(width, height, format, GraphicsFormat.None)
            {
                name = name,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            Texture.Create();
            Handle = RTHandles.Alloc(Texture);
            // GetNativeTexturePtr may synchronize with the render thread: once per target.
            TexturePtr = Texture.GetNativeTexturePtr();
            RenderBufferPtr = Texture.colorBuffer.GetNativeRenderBufferPtr();
        }

        public void Release()
        {
            if (Handle != null)
            {
                RTHandles.Release(Handle);
                Handle = null;
            }
            if (Texture != null)
            {
                Texture.Release();
                UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
            TexturePtr = IntPtr.Zero;
            RenderBufferPtr = IntPtr.Zero;
        }

        /// <summary>The packet target with this texture's own size (absent when there is no texture).</summary>
        public CaelixNativeTarget ToPacket() => Texture == null ? default : new CaelixNativeTarget
        {
            nativeTexture = TexturePtr,
            nativeRenderBuffer = RenderBufferPtr,
            width = (uint)Texture.width,
            height = (uint)Texture.height,
        };

        /// <summary>Recreates the texture when it does not have the size. True when it was replaced.</summary>
        public bool Ensure(string name, GraphicsFormat format, int width, int height)
        {
            if (Matches(width, height)) return false;
            Release();
            Create(name, format, width, height);
            return true;
        }
    }

    /// <summary>
    /// The four targets of one view: colour RGBA16F at the camera size; depth R32F, motion RGBA16F
    /// and albedo RGBA8 UNORM at the geometry size (the camera size, or the upscaler's render size
    /// while the view upscales). These are the library's accepted formats, all with random write.
    /// The library writes them with <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so they have Unity's row order.
    /// The present shader samples every target by UV, so render-size geometry targets need no
    /// change there.
    /// </summary>
    internal sealed class CaelixNativeViewTargets
    {
        public readonly CaelixNativeTargetTexture Color = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Depth = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Motion = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Albedo = new CaelixNativeTargetTexture();

        public bool Matches(int colorWidth, int colorHeight, int geometryWidth, int geometryHeight) =>
            Color.Matches(colorWidth, colorHeight) && Depth.Matches(geometryWidth, geometryHeight) &&
            Motion.Matches(geometryWidth, geometryHeight) && Albedo.Matches(geometryWidth, geometryHeight);

        /// <summary>
        /// Recreates each target that does not have its size: the colour at
        /// <paramref name="colorWidth"/> x <paramref name="colorHeight"/>, the geometry targets at
        /// <paramref name="geometryWidth"/> x <paramref name="geometryHeight"/>. True when any was replaced.
        /// </summary>
        public bool Ensure(int colorWidth, int colorHeight, int geometryWidth, int geometryHeight, int view)
        {
            bool replaced = Color.Ensure($"Caelix Native Colour (view {view})", GraphicsFormat.R16G16B16A16_SFloat,
                colorWidth, colorHeight);
            replaced |= Depth.Ensure($"Caelix Native Depth (view {view})", GraphicsFormat.R32_SFloat,
                geometryWidth, geometryHeight);
            replaced |= Motion.Ensure($"Caelix Native Motion (view {view})", GraphicsFormat.R16G16B16A16_SFloat,
                geometryWidth, geometryHeight);
            replaced |= Albedo.Ensure($"Caelix Native Albedo (view {view})", GraphicsFormat.R8G8B8A8_UNorm,
                geometryWidth, geometryHeight);
            return replaced;
        }

        public void Release()
        {
            Color.Release();
            Depth.Release();
            Motion.Release();
            Albedo.Release();
        }
    }
}
