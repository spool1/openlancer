// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package
// Ported from Librelancer src/Editor/LancerEdit/PreviewRenderer.cs at fork commit 6c9a67395d514da4f37383e6e2ce551171535759.
// docs/VENDORED.md has the Librelancer license text and the OpenLancer changes to this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.IO;
using LibreLancer.Graphics;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Resources;
using LibreLancer.Utf.Mat;
using LibreLancer.World;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Assets;

/// <summary>A model of a thumbnail: the model of an asset of W at a position, for the members of a prop group.</summary>
internal sealed record ThumbnailModel(ModelSource Source, Vector3 Position);

/// <summary>
/// Renders the thumbnail of an asset (section 7.1 of the M23 design): the model of a <see cref="ModelSource"/> of W from
/// the fixed angle of LancerEdit, with two white directional lights, into a square render target. A prop group shows the
/// models of its members at their places.
/// </summary>
/// <remarks>
/// The renderer shares the <c>CommandBuffer</c> and <c>Billboards</c> services of the window with the Scene. So the
/// editor calls it on the UI thread, in the draw of the Assets panel, and never inside the draw-list callback of the
/// Scene (section 7.2).
/// </remarks>
internal sealed class ThumbnailRenderer : IDisposable
{
    /// <summary>The distance of the camera from the center, in radii of the model. The clip preview of the Inspector
    /// uses it too.</summary>
    internal const float CameraDistance = 2.35f;

    /// <summary>A sphere file renders a little smaller, so a planet fits the image.</summary>
    private const float SphereFactor = 1.17f;

    /// <summary>The number of render updates of an image. Static models need no time to settle.</summary>
    private const int Updates = 2;

    /// <summary>The background color of an image. The clip preview of the Inspector uses it too.</summary>
    internal static readonly Color4 NullColor = new(56, 57, 58, 255);

    /// <summary>Two white directional lights, from above and from the front. The clip preview of the Inspector uses them
    /// too.</summary>
    internal static readonly SystemLighting Lighting = MakeLighting();

    private readonly Game game;
    private readonly GameResourceManager resources;
    private readonly FileSystem files;
    private readonly LookAtCamera camera = new();
    private readonly SystemRenderer renderer;

    /// <param name="files">The file system of <paramref name="resources"/>: a view of the edit view.</param>
    public ThumbnailRenderer(Game game, GameResourceManager resources, FileSystem files)
    {
        this.game = game;
        this.resources = resources;
        this.files = files;
        renderer = new SystemRenderer(camera, resources, game)
        {
            NullColor = NullColor,
            SystemLighting = Lighting,
        };
    }

    /// <summary>
    /// Renders the models into a new square texture of a size, or gives null when no model loads. The caller owns the
    /// texture. Its rows have the order of a render target, so the editor draws it with the V coordinates swapped.
    /// </summary>
    /// <param name="radius">The radius of the models around the center of the image, in world units.</param>
    public Texture2D? Render(IReadOnlyList<ThumbnailModel> models, int size, out float radius)
    {
        radius = 0;
        var world = new GameWorld(renderer, null, resources, null, false);
        var objects = new List<GameObject>();
        try
        {
            foreach (var model in models)
            {
                if (Load(model.Source) is not { } resource)
                    continue;
                var modelRadius = 10f;
                if (resource.Drawable is IRigidModelFile rigid)
                    modelRadius = rigid.CreateRigidModel(true, resources).GetRadius();
                if (resource.Drawable is SphFile)
                    modelRadius *= SphereFactor;
                radius = Math.Max(radius, model.Position.Length() + modelRadius);
                var obj = new GameObject(resource, resources, true, false);
                // The archetype constructor of GameObject sets the environment map of a solar archetype the same way.
                if (obj.RenderComponent is ModelRenderer modelRenderer)
                    modelRenderer.EnvMapMaterial = model.Source.EnvMapMaterial;
                obj.SetTransform(new Transform3D(model.Position, Quaternion.Identity));
                world.AddObject(obj);
                objects.Add(obj);
            }
            if (objects.Count == 0)
                return null;
            var angle = Matrix4x4.CreateFromYawPitchRoll(2.62f, -0.24f, 0);
            camera.Update(size, size, Vector3.Transform(new Vector3(0, 0, radius * CameraDistance), angle), Vector3.Zero);
            return Draw(objects, world, size);
        }
        finally
        {
            world.Dispose();
        }
    }

    /// <summary>Loads the libraries and the model of a source from the edit view, as the engine loads a solar
    /// archetype. Null when the model does not load.</summary>
    private ModelResource? Load(ModelSource source)
    {
        if (ThnReferences.PathOf(source.Model) is not { } model)
            return null;
        var resolved = new ResolvedModel
        {
            ModelFile = model,
            SourcePath = source.Model,
            LibraryFiles = source.Libraries.Select(ThnReferences.PathOf).OfType<string>()
                .Where(files.FileExists).ToArray(),
        };
        return files.FileExists(model) ? resolved.LoadFile(resources, MeshLoadMode.GPU | MeshLoadMode.NoCollision) : null;
    }

    private Texture2D Draw(List<GameObject> objects, GameWorld world, int size)
    {
        var context = game.RenderContext;
        var restoreTarget = context.RenderTarget;
        var target = new RenderTarget2D(context, size, size);
        var drawn = false;
        context.RenderTarget = target;
        context.PushViewport(0, 0, size, size);
        try
        {
            context.Cull = true;
            // A work-around of the source of the port: the renderer keeps the object renderers of the earlier image in
            // this list, so each image starts with a new list.
            renderer.objects = new List<ObjectRenderer>();
            foreach (var obj in objects)
                obj.PrepareRender(camera, null, renderer);
            world.Update(1 / 60.0f);
            for (var i = 0; i < Updates; i++)
                world.RenderUpdate(1 / 30.0f);
            renderer.Draw(size, size);
            drawn = true;
        }
        finally
        {
            // The cache catches a failed render and the window draws on, so the render state goes back in each case.
            context.PopViewport();
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
            context.RenderTarget = restoreTarget;
            target.Dispose(keepTexture: drawn);
        }
        return target.Texture;
    }

    public void Dispose() => renderer.Dispose();

    private static SystemLighting MakeLighting()
    {
        var lighting = new SystemLighting();
        foreach (var direction in new[] { new Vector3(0, -1, 0), new Vector3(0, 0, 1) })
        {
            lighting.Lights.Add(new DynamicLight
            {
                Light = new RenderLight { Kind = LightKind.Directional, Direction = direction, Color = Color3f.White },
            });
        }
        return lighting;
    }
}
