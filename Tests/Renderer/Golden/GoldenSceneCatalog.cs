using System.IO;
using System.Linq;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// The reference scenes the suite renders.
    ///
    /// <para><b>What these scenes are built from.</b> The repository ships no playable Source 2 content: the
    /// fixtures under <c>Tests/Files</c> are individual compiled resources kept for the parser tests, not a
    /// game tree. Their external references (materials, textures, child meshes) do not resolve, so lit
    /// geometry here is drawn with the renderer's error material rather than its authored one. That costs
    /// coverage of material parameter binding, texture aliasing and the per-material texture set, and it is
    /// the reason the shader-family scenes below drive shaders that do not need a material. Everything else
    /// -- vertex layouts, skinning, the shadow and depth passes, the light binner, the post-process chain,
    /// the render modes and the readback path -- is exercised for real.</para>
    /// </summary>
    internal static class GoldenSceneCatalog
    {
        /// <summary>Every scene, in the order they are defined.</summary>
        public static IReadOnlyList<GoldenScene> Scenes { get; } = Build();

        // Fixture files, named once so a rename is a compile error rather than a silent skip.
        private const string StaticModel = "n0_lr0_agg_prop_plants001_0.vmdl_c";
        private const string SecondStaticModel = "arch_apartment_ixia_01_top_cap_l_01.vmdl_c";
        private const string AnimatedModel = "axolotl_anim_model_kv3_v2_zstd.vmdl_c";
        private const string IkModel = "box_creature_ik_model.vmdl_c";
        private const string StandaloneMesh = "chen_weapon.vmesh_c";
        private const string SecondStandaloneMesh = "ghostanim_bg_ghostanim_lod0.vmesh_c";
        private const string PhysicsAggregate = "juggernaut.vphys_c";
        private const string SecondPhysicsAggregate = "generic_grip.vphys_c";

        private static List<GoldenScene> Build()
        {
            var scenes = new List<GoldenScene>
            {
                // ---- The frame itself, with nothing in it ----------------------------------------------
                // The cheapest and most sensitive scene in the suite. Nothing is drawn, so the image is
                // purely the background, the tonemap curve and the gamma encode. A nudged clear colour, a
                // changed exposure or a broken sRGB write moves every pixel of it.
                new()
                {
                    Name = "empty_background",
                    Tolerance = ImageTolerance.Strict,
                    Build = static setup => setup.PlaceCamera(new Vector3(0, -100, 40), Vector3.Zero),
                },

                // The frame with the 2D sky switched off, so what remains is the colour clear itself.
                //
                // This scene exists because of a hole found by testing the harness rather than trusting it:
                // nudging the default Framebuffer.ClearColor changed nothing in any of the other scenes,
                // because the sky background covers the whole frame in every one of them and the cleared
                // colour is never sampled. Without this scene the suite has no way to see a wrong clear.
                new()
                {
                    Name = "background_clear_color",
                    Tolerance = ImageTolerance.Strict,
                    Build = static setup =>
                    {
                        AddAxes(setup, 64f);
                        setup.PlaceCamera(new Vector3(90, -110, 70), Vector3.Zero);
                        setup.Renderer.Skybox2D = null;
                    },
                },

                // ---- Procedural geometry, no fixtures needed -------------------------------------------
                new()
                {
                    Name = "lines_world_axes",
                    Tolerance = ImageTolerance.Strict,
                    Build = static setup =>
                    {
                        AddAxes(setup, 64f);
                        setup.PlaceCamera(new Vector3(90, -110, 70), Vector3.Zero);
                    },
                },
                new()
                {
                    Name = "lines_depth_ordering",
                    Tolerance = ImageTolerance.Strict,
                    Build = static setup =>
                    {
                        // Two crossing grids at different depths. A flipped depth comparison swaps which
                        // one wins along the crossings, which no amount of tolerance absorbs.
                        AddGrid(setup, z: 0f, spacing: 16f, extent: 96f, new Color32(1f, 0.3f, 0.3f, 1f));
                        AddGrid(setup, z: 24f, spacing: 24f, extent: 96f, new Color32(0.3f, 0.6f, 1f, 1f));
                        setup.PlaceCamera(new Vector3(60, -140, 55), new Vector3(0, 0, 12));
                    },
                },
                new()
                {
                    Name = "text_msdf_overlay",
                    Tolerance = ImageTolerance.Strict,
                    Build = static setup =>
                    {
                        setup.PlaceCamera(new Vector3(0, -100, 0), Vector3.Zero);

                        // Exercises the font_msdf shader family and the screen-space overlay path.
                        setup.TextRenderer!.AddText(new TextRenderer.TextRenderRequest
                        {
                            X = 12f,
                            Y = 28f,
                            Scale = 20f,
                            Text = "Golden image harness",
                            Color = new Color32(1f, 1f, 1f, 1f),
                        });

                        setup.TextRenderer.AddText(new TextRenderer.TextRenderRequest
                        {
                            X = 12f,
                            Y = 56f,
                            Scale = 14f,
                            Text = "0123456789 gjpqy AVWA",
                            Color = new Color32(1f, 0.8f, 0.2f, 1f),
                        });
                    },
                },
                new()
                {
                    Name = "cubemap_reflection_sphere",
                    Tolerance = ImageTolerance.Lit,
                    Build = static setup =>
                    {
                        // The renderer's own env-cubemap preview sphere: a mirror ball lit only by the
                        // image based light, so the whole cubemap sampling and reflection path is on show.
                        var sphere = ShapeSceneNode.CreateEnvCubemapSphere(setup.Scene);
                        setup.Scene.Add(sphere, dynamic: false);
                        setup.PlaceCamera(new Vector3(0, -70, 12), new Vector3(0, 0, 4));
                    },
                },
            };

            AddModelScenes(scenes);
            AddPhysicsScenes(scenes);
            AddWorldScene(scenes);
            AddParticleScenes(scenes);
            AddMorphScene(scenes);
            AddTextureScenes(scenes);
            AddPostProcessScenes(scenes);
            AddDebugModeScenes(scenes);
            AddGridScene(scenes);

            return scenes;
        }

        /// <summary>
        /// The viewer's infinite reference grid, with something standing in front of it.
        ///
        /// <para>The grid is the only draw in the renderer that computes its own window-space depth, in
        /// <c>grid.frag</c>, rather than taking the one the pipeline interpolates. That makes it the only
        /// draw that can end up in a different depth space from the geometry it is tested against, which
        /// is not hypothetical: a Vulkan frame did exactly that, because the layer depth ranges reached
        /// the backend as <c>glDepthRange</c> calls it never saw, and the grid drew over every model in
        /// the viewer.</para>
        ///
        /// <para>The slab is raised off the plane rather than laid flat on it, so the grid passes both in
        /// front of it and behind it in the same image: a grid that ignores depth covers the slab, and one
        /// written too far back thins out against the horizon. Neither survives the baseline.</para>
        /// </summary>
        /// <remarks>
        /// Appended last, after every other scene, and deliberately. The catalog has a pre-existing order
        /// sensitivity -- the six texture scenes, <c>shadow_sun_cascade</c>,
        /// <c>postprocess_bloom_dof_tonemap</c> and <c>overdraw_heatmap</c> all render differently
        /// depending on what ran before them, which is visible on an untouched checkout by running any one
        /// of them alone and watching it fail against its own baseline. Inserting a scene ahead of them
        /// moves nine baselines for reasons that have nothing to do with the scene being added, so a new
        /// scene goes on the end until that is chased down separately.
        /// </remarks>
        private static void AddGridScene(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                Name = "grid_infinite_occlusion",
                Build = static setup =>
                {
                    AddLitGroundPlane(setup, 150f, z: 30f);
                    setup.PlaceCamera(new Vector3(210, -400, 150), new Vector3(0, 0, 10));
                    setup.EnableBaseGrid = true;
                },
            });
        }

        private static void AddModelScenes(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                Name = "model_static_prop",
                RequiredFixtures = [StaticModel],
                Build = static setup => AddModel(setup, StaticModel, frameFromDistance: 1.6f),
            });

            scenes.Add(new GoldenScene
            {
                Name = "model_static_architecture",
                RequiredFixtures = [SecondStaticModel],
                Build = static setup => AddModel(setup, SecondStaticModel, frameFromDistance: 1.6f),
            });

            scenes.Add(new GoldenScene
            {
                // Sampled a third of a second in, so the animation is genuinely mid-clip rather than on
                // its bind pose. Skinning writes through the same buffers every frame, so a wrong bone
                // matrix or a transposed skinning transform lands here first.
                Name = "model_skinned_animated",
                Frames = 20,
                RequiredFixtures = [AnimatedModel],
                Build = static setup => AddAnimatedModel(setup, AnimatedModel),
            });

            scenes.Add(new GoldenScene
            {
                Name = "model_skinned_ik",
                Frames = 20,
                RequiredFixtures = [IkModel],
                Build = static setup => AddAnimatedModel(setup, IkModel),
            });

            scenes.Add(new GoldenScene
            {
                Name = "mesh_standalone",
                RequiredFixtures = [StandaloneMesh],
                Build = static setup => AddMesh(setup, StandaloneMesh),
            });

            scenes.Add(new GoldenScene
            {
                Name = "mesh_standalone_lod",
                RequiredFixtures = [SecondStandaloneMesh],
                Build = static setup => AddMesh(setup, SecondStandaloneMesh),
            });
        }

        private static void AddPhysicsScenes(List<GoldenScene> scenes)
        {
            // Collision geometry is built on the CPU into the basic_shape shader family, which needs no
            // material. It is the only fixture-driven geometry in the suite that renders with its intended
            // appearance rather than the error material.
            scenes.Add(new GoldenScene
            {
                Name = "shape_physics_hull",
                RequiredFixtures = [PhysicsAggregate],
                Build = static setup => AddPhysics(setup, PhysicsAggregate),
            });

            scenes.Add(new GoldenScene
            {
                Name = "shape_physics_primitives",
                RequiredFixtures = [SecondPhysicsAggregate],
                Build = static setup => AddPhysics(setup, SecondPhysicsAggregate),
            });

            scenes.Add(new GoldenScene
            {
                // The same hull with the sun casting into the shadow map. The scene is framed so the
                // shadow falls across the ground plane lines, where a wrong cascade transform or a
                // reversed depth comparison shows as the shadow moving or vanishing.
                Name = "shadow_sun_cascade",
                RequiredFixtures = [StaticModel],
                Build = static setup => AddShadowCasters(setup),
            });

            scenes.Add(new GoldenScene
            {
                // The same scene, captured from the sun shadow atlas rather than the shaded frame. This is
                // the scene that actually holds the shadow path to account: the cascade's view projection,
                // the depth-only draw of every caster through it, and the reverse-Z orientation of the
                // result are all visible here and none of them are visible in the colour image.
                Name = "shadow_sun_atlas_depth",
                Tolerance = ImageTolerance.Strict,
                RequiredFixtures = [StaticModel],
                Build = static setup =>
                {
                    AddShadowCasters(setup);
                    setup.CaptureShadowAtlas = true;
                },
            });
        }

        /// <summary>
        /// The one particle fixture in the repository that can emit anything.
        /// </summary>
        /// <remarks>
        /// Measured, not assumed: <c>explosion_barrel_kv0_lz4.vpcf_c</c> parses with one
        /// <c>C_OP_RenderSprites</c> renderer and <b>zero emitters</b>, so it never spawns a particle and
        /// no amount of simulation makes it draw. This one has a <c>C_OP_ContinuousEmitter</c>.
        /// </remarks>
        private const string ParticleSystemFixture = "frostivus_throne_wraith_king_ambient_c_b.vpcf_c";

        private static void AddParticleScenes(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                // Continuous sprite emitter, simulated on the fixed timestep, then captured. This is the
                // only route to RenderSprites, one of the three particle draw sites the RHI migration had
                // no coverage for at all.
                //
                // One of only two scenes in the catalog that is not byte-reproducible: re-recording the
                // whole set twice leaves every other baseline identical and moves this one. The particle
                // simulation carries randomness that the harness cannot pin from outside, the way the
                // post-process dither did before it was seeded. Measured across runs the movement is small
                // and steady -- at most 6/255 on any channel, under 0.02% of pixels, mean below 0.01/255 --
                // so the budget clears it with room while still being far tighter than the effect a real
                // change to emission, simulation or the sprite path would have. Seeding that generator
                // would let this scene go to Strict like the rest.
                Name = "particle_sprites",
                Tolerance = ImageTolerance.Accumulating,
                Frames = 40,
                RequiredFixtures = [ParticleSystemFixture],
                Build = static setup =>
                {
                    var resource = setup.LoadFixture(ParticleSystemFixture);

                    if (resource.DataBlock is not ParticleSystem particleSystem)
                    {
                        throw new GoldenRenderException($"'{ParticleSystemFixture}' did not parse as a particle system.");
                    }

                    var node = new ParticleSceneNode(setup.Scene, particleSystem, particleSnapshot: null, preview: true)
                    {
                        Transform = Matrix4x4.Identity,
                    };

                    // The system's material does not resolve in this repository, and a sprite renderer with
                    // no texture draws nothing. Overriding it with the renderer's own flat white is what the
                    // particle viewer does for snapshots, and it makes the geometry the subject of the test
                    // rather than the texture.
                    node.SetTextureOverride(setup.RendererContext.MaterialLoader.GetDefaultColor());

                    setup.Scene.Add(node, dynamic: true);
                    setup.Scene.LightingInfo.UseSceneBoundsForSunLightFrustum = false;

                    setup.PlaceCamera(new Vector3(0, -160, 40), new Vector3(0, 0, 30));

                    // Runs the system forward so the captured frame has a populated bag rather than the
                    // handful of particles a cold start would have emitted.
                    node.Prewarm(setup.Camera);
                },
            });
        }

        private const string MorphFixture = "gsg9_helmet001.vmorf_c";

        /// <summary>
        /// A texture with strong, position-dependent structure, standing in for the morph atlas this
        /// repository does not ship. What it contains does not matter to the composite's logic, but that it
        /// varies sharply from place to place does: it is what makes "which atlas rectangle was read"
        /// visible in the composited image.
        /// </summary>
        private const string MorphAtlasStandIn = "Textures/BC7_testgrid_color_tga_2d6cc34.vtex_c";

        private static void AddMorphScene(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                // The morph composite, rendered and read straight back.
                //
                // WHAT THIS SCENE DOES AND DOES NOT CHECK. It reaches MorphComposite -- construction,
                // Render, and the recorded RHI draw -- and it is the only thing in the suite that does. It
                // is NOT yet an oracle for the BuildVertexBuffer bug, and the captured image is uniform.
                //
                // Measured, not assumed: gsg9_helmet001.vmorf_c parses to exactly one morph with one morph
                // data entry, and that entry carries zero entries in m_morphRectDatas. With no rectangles,
                // allVertices is empty, usedRects stays empty, and Render issues a draw of zero indices
                // into a cleared atlas. There is nothing to composite, so no arrangement of weights or
                // capture windows can make this fixture produce an image.
                //
                // WHAT A FIXTURE WOULD NEED, to turn this into the oracle the morph bug is waiting for:
                //   - a .vmorf_c whose m_morphRectDatas holds at least three rectangles, since the bug is
                //     invisible whenever the active rectangles are already the lowest-numbered ones;
                //   - its m_pTextureAtlas .vtex_c alongside it, or the stand-in registered below;
                //   - ideally a morph whose rectangles can be driven independently, so a weighting that
                //     activates only the higher-numbered rectangles can be composited. That is the case
                //     that separates correct behaviour from the current behaviour, because Render uploads
                //     from the front of allVertices rather than from the rectangles in use.
                // With that in place this scene needs no code change: raise the weights, record, and the
                // baseline pins which rectangles were uploaded.
                //
                // Not a shaded scene, because no fixture here has a model that samples a morph composite.
                // The composite is captured on its own instead, which is what makes any coverage possible.
                Name = "morph_composite_atlas",
                Tolerance = ImageTolerance.Strict,
                RequiredFixtures = [MorphFixture, MorphAtlasStandIn],
                Build = static setup =>
                {
                    var resource = setup.LoadFixture(MorphFixture);

                    if (resource.DataBlock is not Morph morph)
                    {
                        throw new GoldenRenderException($"'{MorphFixture}' did not parse as morph data.");
                    }

                    var atlasPath = morph.Data.GetStringProperty("m_pTextureAtlas");
                    setup.FileLoader!.Substitute(atlasPath, MorphAtlasStandIn);

                    try
                    {
                        morph.LoadFlexData(setup.RendererContext.FileLoader);
                    }
                    catch (NotImplementedException)
                    {
                        // VRF cannot parse this fixture's flex rules -- it rejects the "jawOpen" flex
                        // controller type outright. The texture atlas is resolved before the rules are
                        // walked, so the morph is still usable for compositing, which is all this scene
                        // needs. Swallowed narrowly and deliberately: the alternative is no morph coverage
                        // at all until that parser gains a case.
                    }

                    if (morph.TextureResource == null)
                    {
                        throw new GoldenRenderException(
                            $"The morph atlas stand-in was not picked up for '{atlasPath}'.");
                    }

                    var composite = new MorphComposite(setup.RendererContext, morph);

                    // Every morph driven to full, so the composite is built from all the rectangles rather
                    // than from whichever subset a partial weighting would leave active.
                    //
                    // Set twice, and that is not redundant. MorphComposite.SetMorphValue decides whether a
                    // rectangle is in use from GetMorphValue(morphId) -- the value already stored -- before
                    // writing the new one, so the first call to raise a morph off zero writes the weight and
                    // then *removes* its rectangles from usedRects. One call therefore composites nothing at
                    // all; the second sees the weight the first wrote and adds them back. Found here, by
                    // this scene rendering a completely empty atlas.
                    for (var morphId = 0; morphId < morph.GetMorphCount(); morphId++)
                    {
                        composite.SetMorphValue(morphId, 1f);
                        composite.SetMorphValue(morphId, 1f);
                    }

                    setup.MorphComposite = composite;
                    setup.PlaceCamera(new Vector3(0, -100, 0), Vector3.Zero);
                },
            });
        }

        private static void AddWorldScene(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                // Stands in for a world map chunk. A real one cannot be built here: the only world node
                // fixture in the repository resolves none of the models it references, so loading it
                // produces an empty scene whose image is indistinguishable from an empty frame and would
                // therefore never fail. This builds the same shape of workload by hand -- many static
                // instances spread over a large volume -- which is what actually stresses the static
                // octree, the instance transform buffers, the indirect draw path and frustum culling.
                Name = "world_static_batch",
                Tolerance = ImageTolerance.Lit,
                RequiredFixtures = [StaticModel, SecondStaticModel],
                Build = static setup =>
                {
                    var models = new[] { LoadModel(setup, StaticModel), LoadModel(setup, SecondStaticModel) };
                    var index = 0;

                    for (var x = -2; x <= 2; x++)
                    {
                        for (var y = -2; y <= 2; y++)
                        {
                            var node = new ModelSceneNode(setup.Scene, models[index % models.Length])
                            {
                                // Yaw varies with the cell so the instances are not all the same silhouette,
                                // which is what makes a wrong instance transform visible rather than hidden
                                // behind identical copies.
                                Transform = Matrix4x4.CreateRotationZ(index * 0.4f)
                                    * Matrix4x4.CreateTranslation(x * 160f, y * 160f, 0f),
                            };

                            setup.Scene.Add(node, dynamic: false);
                            index++;
                        }
                    }

                    setup.PlaceCamera(new Vector3(340, -560, 420), new Vector3(0, 0, 40));
                },
            });
        }

        /// <summary>
        /// Renders a single texture on an unlit preview quad, one scene per compressed format. Unlit on
        /// purpose: the pixel that lands in the image is the decoded texel plus the tonemap, with no
        /// lighting term in between, so a wrong block decoder or a swapped channel order shows up directly.
        /// </summary>
        private static void AddTextureScenes(List<GoldenScene> scenes)
        {
            // DXT1 is missing from this list on purpose, and the reason is a finding rather than an
            // omission. The only DXT1 fixture in the repository is
            // DXT1_dota_default_cube_tga_c95513b9.vtex_c, and on this quad the two backends disagree about
            // it completely: the OpenGL path samples flat grey, while the RHI path samples the texture
            // correctly. 88% of pixels differ, mean deviation 35.7/255 -- not a tolerance question.
            //
            // Whatever the cause, a baseline recorded from the OpenGL path would enshrine the wrong image,
            // and one recorded from the RHI path would fail the default mode. The scene was therefore
            // withdrawn rather than left encoding either. Restoring DXT1 coverage needs a plain 2D DXT1
            // fixture, and the divergence on this one needs explaining first.
            //
            // Narrowing that, for whoever picks it up: this fixture is the only CUBE texture among the
            // format set, and every fixture that agrees is 2D. So the variable under test is almost
            // certainly the texture target rather than the block format -- a cube sampled through the
            // viewer's 2D quad -- and the two paths reaching different answers about it says the target
            // is carried differently by RenderTexture's own binding than by the RHI's. That makes it a
            // cube-versus-2D binding question, not a DXT1 decode question, and it is worth confirming
            // that way round before anyone goes looking at the decompressor.
            var textures = new[]
            {
                ("texture_dxt5", "DXT5_mod_dire_lava_000b_vmat_g_tnormal1_5a28bd86.vtex_c"),
                ("texture_bc7", "BC7_testgrid_color_tga_2d6cc34.vtex_c"),
                ("texture_bc6h_hdr", "BC6H.vtex_c"),
                ("texture_ati1n", "ATI1N_testgrid_morph_vmat_g_tsquishambientocclusion_57d513e.vtex_c"),
                ("texture_ati2n", "ATI2N_hotel_tarp_001_freedom_psd_993397bd.vtex_c"),
                ("texture_etc2", "ETC2_banner_s0_lvl0_color_psd_953a8d49.vtex_c"),
                ("texture_rgba8888", "RGBA8888_red_large_on_png.vtex_c"),
                ("texture_rgba16f", "RGBA16161616F.vtex_c"),
            };

            foreach (var (name, fileName) in textures)
            {
                var fixturePath = Path.Combine("Textures", fileName);

                scenes.Add(new GoldenScene
                {
                    Name = name,
                    Tolerance = ImageTolerance.Strict,
                    RequiredFixtures = [fixturePath],
                    Build = setup =>
                    {
                        var resource = setup.LoadFixture(fixturePath);
                        var texture = setup.RendererContext.MaterialLoader.LoadTexture(resource, srgbRead: true);

                        var material = new RenderMaterial(
                            setup.RendererContext.ShaderLoader.LoadShader("vr_unlit"));

                        // The opaque variant of vr_unlit samples g_tColor2; g_tColor only exists in its
                        // translucent and alpha-tested variants.
                        material.LoadRenderState();
                        material.Textures["g_tColor2"] = texture;

                        var quad = MeshSceneNode.CreateMaterialPreviewQuad(setup.Scene, material, new Vector2(100f, 100f));

                        // The preview quad is built in the XY plane facing +Z. Stood upright it faces -Y,
                        // which is the axis the camera below looks along.
                        quad.Transform = Matrix4x4.CreateRotationX(MathF.PI / 2f);

                        setup.Scene.Add(quad, dynamic: false);

                        // Square on, filling the frame: no perspective foreshortening to blur the texels
                        // being checked.
                        setup.PlaceCamera(new Vector3(0, -58, 0), Vector3.Zero);
                    },
                });
            }
        }

        private static void AddPostProcessScenes(List<GoldenScene> scenes)
        {
            scenes.Add(new GoldenScene
            {
                // Bloom, depth of field and the tonemap running together over real geometry. Each stage
                // reads the one before it, so this is where a broken intermediate target or a wrong
                // resolve shows up as an image that is merely plausible rather than correct.
                Name = "postprocess_bloom_dof_tonemap",
                Tolerance = ImageTolerance.Accumulating,
                RequiredFixtures = [PhysicsAggregate],
                Build = static setup =>
                {
                    AddPhysics(setup, PhysicsAggregate);
                    AddLitGroundPlane(setup, 400f);
                    setup.PlaceCamera(new Vector3(90, -130, 70), new Vector3(0, 0, 20));

                    var postProcess = setup.Renderer.Postprocess;

                    postProcess.DOF.Enabled = true;
                    postProcess.DOF.FocalDistance = 120f;
                    postProcess.DOF.FarBlurry = 400f;
                    postProcess.DOF.NearBlurry = -120f;

                    // Pinned rather than adapted: auto exposure integrates over frames and would make the
                    // captured image depend on how many were rendered.
                    postProcess.CustomExposure = 1.0f;

                    setup.EnableBloomAfterRender = true;
                },
            });

            scenes.Add(new GoldenScene
            {
                // The tonemap alone, over a bright surface, with post-processing otherwise untouched.
                // Separating it from the scene above means a tonemap regression is attributable.
                Name = "postprocess_tonemap_only",
                Tolerance = ImageTolerance.Lit,
                RequiredFixtures = [PhysicsAggregate],
                Build = static setup =>
                {
                    AddPhysics(setup, PhysicsAggregate);
                    setup.PlaceCamera(new Vector3(60, -90, 45), new Vector3(0, 0, 15));
                    setup.Renderer.Postprocess.CustomExposure = 2.0f;
                },
            });
        }

        private static void AddDebugModeScenes(List<GoldenScene> scenes)
        {
            // One scene per debug visualisation. These bypass the tonemap and write display colours
            // directly, which makes them unusually sharp detectors: the images are flat regions of exact
            // colour, so any change at all in what the shader computes is visible immediately.
            var debugModes = new[]
            {
                ("debug_mode_normals", "Normals"),
                ("debug_mode_color", "Color"),
                ("debug_mode_illumination", "Illumination"),
                ("debug_mode_overdraw", "Overdraw"),
            };

            foreach (var (name, mode) in debugModes)
            {
                var renderMode = mode;

                scenes.Add(new GoldenScene
                {
                    Name = name,
                    Tolerance = ImageTolerance.Strict,
                    RequiredFixtures = [PhysicsAggregate],
                    Build = setup =>
                    {
                        AddPhysics(setup, PhysicsAggregate);
                        setup.PlaceCamera(new Vector3(60, -90, 45), new Vector3(0, 0, 15));
                        setup.SetRenderMode(renderMode);
                    },
                });
            }

            scenes.Add(new GoldenScene
            {
                // The Overdraw mode actually driven, rather than merely selected. The census showed that
                // debug_mode_overdraw above never touches QuadOverdraw at all: selecting the mode only
                // swaps the scene's replacement shader, while the depth prime, counting and resolve passes
                // are driven by the caller around the render. This scene drives them.
                //
                // Deliberately overlapping geometry, since a heat map of a scene with no overdraw in it is
                // a uniform image that would not notice the counter being wrong.
                //
                // :OverdrawSceneOrderDependence - do not debug this scene by running it on its own, and do
                // not widen its tolerance on what you see when you do. Run alone it fails its baseline by
                // max 195/255, mean 2.38/255 and 4.15% of pixels -- 40x its budget -- and does so
                // identically on every run, so it is not the counting race. Run in catalog order it lands
                // at max 135/255, mean 0.05-0.06/255 and 0.08-0.11% of pixels, comfortably inside
                // ImageTolerance.CountingRace, which was re-measured over the runs that established this.
                //
                // The dependence goes both ways, and the other direction is the serious one: forcing this
                // scene to run first and leaving every other scene in catalog order takes the suite from
                // 36/36 to 27/36. shadow_sun_cascade, postprocess_bloom_dof_tonemap and six of the
                // texture_* scenes all move, several of them by more than 20% of their pixels, and the
                // texture scenes move in identical pairs -- bc7 with bc6h_hdr, rgba8888 with rgba16f --
                // which reads as them rendering each other's content rather than as noise.
                //
                // So the scenes in this catalog are not independent: this one leaves global state behind,
                // and the baselines encode the order below. Most likely QuadOverdraw.Prepare's
                // glBindImageTexture on units 3 and 4, which nothing ever unbinds and which outlive the
                // Renderer that caused them, but that is a hypothesis and the fix would live in
                // Renderer/QuadOverdraw.cs rather than here. Recorded rather than fixed because a fix
                // moves baselines, and which order is the correct one to record is a decision this file
                // cannot make on its own.
                Name = "overdraw_heatmap",
                Tolerance = ImageTolerance.CountingRace,
                RequiredFixtures = [PhysicsAggregate],
                Build = static setup =>
                {
                    AddPhysics(setup, PhysicsAggregate, Matrix4x4.Identity);
                    AddPhysics(setup, PhysicsAggregate, Matrix4x4.CreateRotationZ(0.9f) * Matrix4x4.CreateTranslation(0f, 0f, 12f));
                    AddLitGroundPlane(setup, 300f);

                    setup.PlaceCamera(new Vector3(70, -110, 55), new Vector3(0, 0, 15));
                    setup.EnableQuadOverdraw = true;
                },
            });

            scenes.Add(new GoldenScene
            {
                // Occlusion culling on, with the occluded-bounds overlay requested, and enough frames to
                // clear the renderer's one-second warmup. Geometry is a wall of instances with more behind
                // it, so there is something for a depth pyramid to occlude.
                //
                // It does not currently reach OcclusionDebugRenderer, and cannot from this repository.
                // Measured rather than assumed: SceneMeshletCount is 0 in every scene in this catalog.
                // Meshlets are built only from SceneAggregate nodes, which only the world loader creates
                // from world node aggregate geometry, and no fixture here resolves any. With no meshlets
                // DrawMeshletsIndirect stays false, and the overlay's first line returns on it -- as do the
                // GPU meshlet cull, the indirect draw path, draw compaction and the depth pyramid.
                //
                // Kept because it is still a real multi-instance culling scene over 70 frames, and because
                // it starts covering all of the above the moment aggregate geometry is available.
                Name = "occlusion_debug_overlay",
                Tolerance = ImageTolerance.Lit,
                Frames = 70,
                RequiredFixtures = [StaticModel],
                Build = static setup =>
                {
                    var model = LoadModel(setup, StaticModel);

                    for (var row = 0; row < 4; row++)
                    {
                        for (var column = -2; column <= 2; column++)
                        {
                            setup.Scene.Add(new ModelSceneNode(setup.Scene, model)
                            {
                                Transform = Matrix4x4.CreateTranslation(column * 70f, row * 120f, 0f),
                            }, dynamic: false);
                        }
                    }

                    setup.PlaceCamera(new Vector3(0, -260, 60), new Vector3(0, 200, 40));
                    setup.EnableOcclusionDebug = true;
                },
            });
        }

        // ---- Shared scene building blocks ---------------------------------------------------------------

        private static Model LoadModel(GoldenSceneSetup setup, string fixture)
        {
            var resource = setup.LoadFixture(fixture);

            return resource.DataBlock as Model
                ?? throw new GoldenRenderException($"'{fixture}' did not parse as a model.");
        }

        private static void AddModel(GoldenSceneSetup setup, string fixture, float frameFromDistance)
        {
            var node = new ModelSceneNode(setup.Scene, LoadModel(setup, fixture));
            setup.Scene.Add(node, dynamic: false);

            FrameNode(setup, node, frameFromDistance);
        }

        private static void AddAnimatedModel(GoldenSceneSetup setup, string fixture)
        {
            var node = new ModelSceneNode(setup.Scene, LoadModel(setup, fixture));
            setup.Scene.Add(node, dynamic: true);

            // Sorted rather than taken in load order, so the clip that gets played is a property of the
            // fixture and not of how the loader happened to enumerate it.
            var animation = node.Animations.Keys.Order(StringComparer.Ordinal).FirstOrDefault();

            if (animation != null)
            {
                node.SetAnimationByName(animation);
            }

            FrameNode(setup, node, 1.8f);
        }

        private static void AddMesh(GoldenSceneSetup setup, string fixture)
        {
            var resource = setup.LoadFixture(fixture);

            if (resource.DataBlock is not Mesh mesh)
            {
                throw new GoldenRenderException($"'{fixture}' did not parse as a mesh.");
            }

            var node = new MeshSceneNode(setup.Scene, mesh, 0);
            setup.Scene.Add(node, dynamic: false);

            FrameNode(setup, node, 1.8f);
        }

        private static void AddPhysics(GoldenSceneSetup setup, string fixture, Matrix4x4? transform = null)
        {
            var resource = setup.LoadFixture(fixture);

            if (resource.DataBlock is not PhysAggregateData physics)
            {
                throw new GoldenRenderException($"'{fixture}' did not parse as physics data.");
            }

            SceneNode? last = null;

            foreach (var node in PhysSceneNode.CreatePhysSceneNodes(setup.Scene, physics, fixture))
            {
                // Collision nodes start hidden; the viewer turns them on from its physics toggle.
                node.Enabled = true;

                // Drawn as solid geometry rather than the viewer's translucent overlay, so the image is a
                // function of the geometry alone and not of how the translucent pass happened to sort it.
                node.IsTranslucentRenderMode = false;

                if (transform.HasValue)
                {
                    node.Transform = transform.Value;
                }

                setup.Scene.Add(node, dynamic: false);
                last = node;
            }

            if (last != null && !transform.HasValue)
            {
                FrameNode(setup, last, 2.2f);
            }
        }

        /// <summary>
        /// A lit ground plane with model geometry standing above it: the arrangement the sun shadow pass
        /// needs, since only mesh draw calls are submitted as shadow casters and the collision hulls used
        /// elsewhere in the catalog are not meshes.
        /// </summary>
        private static void AddShadowCasters(GoldenSceneSetup setup)
        {
            var model = LoadModel(setup, StaticModel);

            for (var i = -1; i <= 1; i++)
            {
                var node = new ModelSceneNode(setup.Scene, model)
                {
                    Transform = Matrix4x4.CreateTranslation(i * 90f, 0f, 60f),
                };

                setup.Scene.Add(node, dynamic: false);
            }

            AddLitGroundPlane(setup, 400f);
            setup.PlaceCamera(new Vector3(150, -260, 150), new Vector3(0, 0, 30));
        }

        /// <summary>
        /// Adds a flat lit surface at z = 0 through the main <c>complex</c> shader family. A shadow needs
        /// something to fall on, and nothing else in the catalog receives one: the collision hulls draw
        /// through <c>basic_shape</c> and the debug lines are unlit.
        /// </summary>
        private static void AddLitGroundPlane(GoldenSceneSetup setup, float size, float z = 0f)
        {
            var material = new RenderMaterial(setup.RendererContext.ShaderLoader.LoadShader("complex"));
            material.LoadRenderState();

            var ground = MeshSceneNode.CreateMaterialPreviewQuad(setup.Scene, material, new Vector2(size, size));

            if (z != 0f)
            {
                ground.Transform = Matrix4x4.CreateTranslation(0f, 0f, z);
            }

            setup.Scene.Add(ground, dynamic: false);
        }

        /// <summary>Frames everything in the scene at once, for scenes built from many nodes.</summary>
        private static void FrameScene(GoldenSceneSetup setup, float distanceScale)
        {
            var bounds = default(AABB);
            var first = true;

            foreach (var node in setup.Scene.AllNodes)
            {
                bounds = first ? node.BoundingBox : bounds.Union(node.BoundingBox);
                first = false;
            }

            if (first)
            {
                setup.PlaceCamera(new Vector3(0, -100, 40), Vector3.Zero);
                return;
            }

            FrameBounds(setup, bounds, distanceScale);
        }

        /// <summary>
        /// Places the camera along a fixed diagonal at a multiple of the node's bounding radius. The angle
        /// is constant, so the shot only moves if the geometry's own bounds move.
        /// </summary>
        private static void FrameNode(GoldenSceneSetup setup, SceneNode node, float distanceScale)
            => FrameBounds(setup, node.BoundingBox, distanceScale);

        private static void FrameBounds(GoldenSceneSetup setup, AABB bounds, float distanceScale)
        {
            var center = bounds.Center;
            var radius = MathF.Max(1f, (bounds.Max - bounds.Min).Length() * 0.5f);
            var distance = radius * distanceScale;

            var direction = Vector3.Normalize(new Vector3(0.6f, -1f, 0.45f));
            setup.PlaceCamera(center + direction * distance, center);
        }

        private static void AddAxes(GoldenSceneSetup setup, float length)
        {
            setup.Scene.Add(new LineSceneNode(setup.Scene, Vector3.Zero, new Vector3(length, 0, 0),
                new Color32(1f, 0.15f, 0.15f, 1f), new Color32(1f, 0.15f, 0.15f, 1f)), dynamic: false);

            setup.Scene.Add(new LineSceneNode(setup.Scene, Vector3.Zero, new Vector3(0, length, 0),
                new Color32(0.15f, 1f, 0.15f, 1f), new Color32(0.15f, 1f, 0.15f, 1f)), dynamic: false);

            setup.Scene.Add(new LineSceneNode(setup.Scene, Vector3.Zero, new Vector3(0, 0, length),
                new Color32(0.3f, 0.4f, 1f, 1f), new Color32(0.3f, 0.4f, 1f, 1f)), dynamic: false);
        }

        private static void AddGrid(GoldenSceneSetup setup, float z, float spacing, float extent, Color32 color)
        {
            for (var offset = -extent; offset <= extent; offset += spacing)
            {
                setup.Scene.Add(new LineSceneNode(setup.Scene,
                    new Vector3(-extent, offset, z), new Vector3(extent, offset, z), color, color), dynamic: false);

                setup.Scene.Add(new LineSceneNode(setup.Scene,
                    new Vector3(offset, -extent, z), new Vector3(offset, extent, z), color, color), dynamic: false);
            }
        }
    }
}
