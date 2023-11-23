using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Terraria;
using Terraria.Graphics.Light;
using Terraria.ModLoader;

namespace Zenith.Content.Features.Lighting.StaticLightMap;

/// <summary>
///     Refer to the <c>README.md</c> for an explanation of the philosophy
///     behind this optimization.
/// </summary>
internal sealed class StaticLightMapSystem : ModSystem
{
    /* Lighting Engine
     * if (!this._activeProcessedArea.Contains(x, y))
     *   return Vector3.Zero;
     * x -= this._activeProcessedArea.X;
     * y -= this._activeProcessedArea.Y;
     * return this._activeLightMap[x, y];
     */

    /* Legacy Lighting Engine
     * int index1 = x - this._requestedRectLeft + Lighting.OffScreenTiles;
     * int index2 = y - this._requestedRectTop + Lighting.OffScreenTiles;
     * Vector2 unscaledSize = this._camera.UnscaledSize;
     * if (index1 < 0 || index2 < 0 || (double) index1 >= (double) unscaledSize.X / 16.0 + (double) (Lighting.OffScreenTiles * 2) + 10.0 || (double) index2 >= (double) unscaledSize.Y / 16.0 + (double) (Lighting.OffScreenTiles * 2))
     *   return Vector3.Zero;
     * LegacyLighting.LightingState lightingState = this._states[index1][index2];
     * return new Vector3(lightingState.R, lightingState.G, lightingState.B);
     */

    private static Vector3[] lightMap = Array.Empty<Vector3>();
    private static int lightMapHeight;
    private static Rectangle mapBounds;
    private static Point preOffset;
    private static Point postOffset;

    public override void Load()
    {
        base.Load();

        On_LightingEngine.Present += PresentLightingEngineLightMap;
        On_LegacyLighting.ProcessArea += ProcessLegacyLightingLightMap;

        // AT RISK OF INLINING:
        IL_Lighting.GetColor_Point += DevirtualizeGetColorCalls;
        // AT RISK OF INLINING:
        IL_Lighting.GetColor_Point_Color += DevirtualizeGetColorCalls;
        // AT RISK OF INLINING:
        IL_Lighting.GetColor_int_int_Color += DevirtualizeGetColorCalls;
        // The rest are okay:
        IL_Lighting.GetColor_int_int += DevirtualizeGetColorCalls;
        IL_Lighting.GetColor9Slice_int_int_refColorArray += DevirtualizeGetColorCalls;
        IL_Lighting.GetColor9Slice_int_int_refVector3Array += DevirtualizeGetColorCalls;
        IL_Lighting.GetCornerColors += DevirtualizeGetColorCalls;
        IL_Lighting.GetColor4Slice_int_int_refColorArray += DevirtualizeGetColorCalls;
        IL_Lighting.GetColor4Slice_int_int_refVector3Array += DevirtualizeGetColorCalls;
    }

    private static void DevirtualizeGetColorCalls(ILContext il)
    {
        ILCursor c = new(il);

        if (!c.TryGotoNext(MoveType.Before, i => i.MatchCallvirt(typeof(ILightingEngine).GetMethod(nameof(ILightingEngine.GetColor))!)))
            return;

        // It's generally ill-advised to remove opcodes, but it's fine here.
        // We're completely replacing it with our own call and don't want to
        // leave any remnant to ensure our performance optimizations actually
        // matter. I don't want to leave it up to the JITer to figure out the
        // best course of action here.
        c.Remove();
        c.Emit(OpCodes.Call, typeof(StaticLightMapSystem).GetMethod(nameof(GetColorWrapper), BindingFlags.NonPublic | BindingFlags.Static)!);
    }

    // Both methods are decorated with aggressive inlining to ensure they get
    // inlined as expected within the methods they're placed in. At the very
    // least the wrapper will keep this attribute for sanity's sake (though the
    // JITer should know to just inline here).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 GetColorWrapper(ILightingEngine _, int x, int y)
    {
        return GetColor(x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 GetColor(int x, int y)
    {
        x -= preOffset.X;
        y -= preOffset.Y;

        if (!mapBounds.Contains(x, y))
            return Vector3.Zero;

        x -= postOffset.X;
        y -= postOffset.Y;

        return lightMap[IndexOf(x, y, lightMapHeight)];
    }

    private static void PresentLightingEngineLightMap(On_LightingEngine.orig_Present orig, LightingEngine self)
    {
        orig(self);

        preOffset = Point.Zero;
        postOffset = new Point(self._activeProcessedArea.X, self._activeProcessedArea.Y);
        mapBounds = self._activeProcessedArea;
        lightMap = self._activeLightMap._colors;
        lightMapHeight = self._activeLightMap.Height;
    }

    private static void ProcessLegacyLightingLightMap(On_LegacyLighting.orig_ProcessArea orig, LegacyLighting self, Rectangle area)
    {
        orig(self, area);

        Vector2 unscaledSize = self._camera.UnscaledSize;

        preOffset = new Point(self._requestedRectLeft + Terraria.Lighting.OffScreenTiles, self._requestedRectTop + Terraria.Lighting.OffScreenTiles);
        postOffset = Point.Zero;
        mapBounds = new Rectangle(0, 0, (int) (unscaledSize.X / 16.0 + Terraria.Lighting.OffScreenTiles * 2 + 10), (int) (unscaledSize.Y / 16.0 + Terraria.Lighting.OffScreenTiles * 2));
        lightMap = new Vector3[mapBounds.Width * mapBounds.Height];
        lightMapHeight = mapBounds.Height;

        for (int x = 0; x < mapBounds.Width; x++)
        {
            for (int y = 0; y < mapBounds.Height; y++)
            {
                LegacyLighting.LightingState lightingState = self._states[x][y];
                lightMap[IndexOf(x, y, lightMapHeight)] = new Vector3(lightingState.R, lightingState.G, lightingState.B);
            }
        }
    }

    private static int IndexOf(int x, int y, int height) => x * height + y;
}
