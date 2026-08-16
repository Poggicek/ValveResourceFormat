using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;

namespace Tests.Renderer
{
    public class VertexAttributeLocationsTest
    {
        /// <summary>
        /// Buffer semantic ("TEXCOORD", 3) is the lightmap UV stream, not a third generic UV set.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The slot names read as if this element belonged to <see cref="VertexSlot.TexCoord3"/>, and it
        /// does not: no shipped material names it as a third texture coordinate. It is
        /// <c>vLightmapUV</c> in 19965 of the 20095 CS2 materials that name it and in 3849 of Half-Life:
        /// Alyx's 4079; the remainder are <c>vFoliageParams</c> and sprite card sequence data, both of
        /// which the name table resolves before the semantic is ever consulted.
        /// </para>
        /// <para>
        /// The semantic is what a world material that does not name the element falls back to, and world
        /// materials routinely do not name it -- <c>materials/dev/reflectivity_30.vmat</c> is one, and a
        /// material that fails to load has no signature at all. Both cases still draw a mesh carrying the
        /// stream, under a draw call flagged as lit from the lightmap, so the shader reads
        /// <c>vLightmapUV</c>. Resolving to <see cref="VertexSlot.TexCoord3"/> there put the stream where
        /// nothing reads it and left the lightmap sampled at whatever an unsupplied attribute yields.
        /// </para>
        /// </remarks>
        [Test]
        public void LightmapUvOwnsTexcoord3()
        {
            Assert.That(VertexAttributeLocations.Get("TEXCOORD", 3), Is.EqualTo((int)VertexSlot.LightmapUV));

            var lightmapUv = new VBIB.RenderInputLayoutField("TEXCOORD", DXGI_FORMAT.R16G16_UNORM, 0)
            {
                SemanticIndex = 3,
            };

            // No signature at all, which is what an unresolvable material leaves behind
            Assert.That(
                VertexAttributeLocations.Resolve(Material.VsInputSignature.Empty, lightmapUv, out var signatureName),
                Is.EqualTo((int)VertexSlot.LightmapUV));

            Assert.That(signatureName, Is.Empty);

            // Both spellings still reach the slot by name, and the slot the index suggests keeps its own
            Assert.That(VertexAttributeLocations.Get("vLightmapUV"), Is.EqualTo((int)VertexSlot.LightmapUV));
            Assert.That(VertexAttributeLocations.Get("vLightmapUVW"), Is.EqualTo((int)VertexSlot.LightmapUV));
            Assert.That(VertexAttributeLocations.Get("vTEXCOORD3"), Is.EqualTo((int)VertexSlot.TexCoord3));
            Assert.That(VertexAttributeLocations.Get("vFoliageParams"), Is.EqualTo((int)VertexSlot.TexCoord3));
        }
    }
}
