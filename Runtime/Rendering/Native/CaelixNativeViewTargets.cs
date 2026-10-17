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

        public CaelixNativeTarget ToPacket(int width, int height) => new CaelixNativeTarget
        {
            nativeTexture = TexturePtr,
            nativeRenderBuffer = RenderBufferPtr,
            width = (uint)width,
            height = (uint)height,
        };
    }

    /// <summary>
    /// The four targets of one view at the view's camera size: colour RGBA16F, depth R32F, motion
    /// RGBA16F, albedo RGBA8 UNORM (the library's accepted formats), all with random write. The
    /// library writes them with <c>CAELIX_RENDER_FLAG_FLIP_Y</c>, so they have Unity's row order.
    /// </summary>
    internal sealed class CaelixNativeViewTargets
    {
        public readonly CaelixNativeTargetTexture Color = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Depth = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Motion = new CaelixNativeTargetTexture();
        public readonly CaelixNativeTargetTexture Albedo = new CaelixNativeTargetTexture();

        public bool Matches(int width, int height) =>
            Color.Matches(width, height) && Depth.Matches(width, height) &&
            Motion.Matches(width, height) && Albedo.Matches(width, height);

        /// <summary>Recreates all four when any does not match the size. True when they were replaced.</summary>
        public bool Ensure(int width, int height, int view)
        {
            if (Matches(width, height)) return false;
            Release();
            Color.Create($"Caelix Native Colour (view {view})", GraphicsFormat.R16G16B16A16_SFloat, width, height);
            Depth.Create($"Caelix Native Depth (view {view})", GraphicsFormat.R32_SFloat, width, height);
            Motion.Create($"Caelix Native Motion (view {view})", GraphicsFormat.R16G16B16A16_SFloat, width, height);
            Albedo.Create($"Caelix Native Albedo (view {view})", GraphicsFormat.R8G8B8A8_UNorm, width, height);
            return true;
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
