using UnityEngine;

namespace EZXRNativeRenderring
{
    public static class NativeAPI
    {
        public static void startEZXRRenderer() { Debug.Log("[EZXR Stub] startEZXRRenderer called - no-op on Windows"); }
        public static void setFrameInfo(int a, int b, int c, int d) { }
        public static void stopEZXRRenderer() { }
        public static void hotConfigEZXRRenderer(int a, int b, int c, int d) { }
    }
}
