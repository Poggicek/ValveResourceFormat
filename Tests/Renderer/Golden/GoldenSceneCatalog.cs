using System.IO;
using System.Linq;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.ResourceTypes;

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
            AddTextureScenes(scenes);
            AddPostProcessScenes(scenes);
            AddDebugModeScenes(scenes);

            return scenes;
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
            var textures = new[]
            {
                ("texture_dxt1", "DXT1_dota_default_cube_tga_c95513b9.vtex_c"),
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
        private static void AddLitGroundPlane(GoldenSceneSetup setup, float size)
        {
            var material = new RenderMaterial(setup.RendererContext.ShaderLoader.LoadShader("complex"));
            material.LoadRenderState();

            var ground = MeshSceneNode.CreateMaterialPreviewQuad(setup.Scene, material, new Vector2(size, size));
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
