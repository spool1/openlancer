# Vendored Librelancer files

OpenLancer copies some files from Librelancer into its own projects. This file lists each copied file, its Librelancer source and the changes that OpenLancer made to it. The copies were made when the submodule could not change. Now the engine is our fork (docs/UPSTREAM.md), so a new port is a copy or a fork commit, whichever costs less at the next upstream merge. Before you port a LancerEdit class, compare the copy with a fork commit that removes its `MainWindow` dependency.

In vendored file headers, LICENSE means the Librelancer license below.

The files below come from Librelancer commit `dcbcf8080dec4a10ebe1ef28b89a0a6d122fe415`, the base commit of the fork. The port `ThumbnailRenderer.cs` comes from the fork commit `6c9a67395d514da4f37383e6e2ce551171535759`. Each file starts with a Librelancer copyright header and a line that names its source path and its commit.

| OpenLancer path | Librelancer source path | OpenLancer changes |
|---|---|---|
| `src/OpenLancer.Editor/Scene/GridRenderer.cs` | `src/Editor/LancerEdit/Model/GridRender.cs` | An instance class with `IDisposable` that `SceneView` owns, not a static class. It loads its shader with `ShaderBundle.FromResource<GridRenderer>(context, "Grid.bin")`, because `EditorShaders` is not vendored. A new uniform block for the changed fragment shader. `LevelsFor` replaces the step table `DistanceScale`: it calculates the line spacing, the opacity of the fine lines and the fade distance from the camera height, so the grid does not jump between levels. `Draw` takes the axis colors and no near and far planes. File-scoped namespace `OpenLancer.Editor.Scene` and nullable annotations. |
| `src/OpenLancer.Editor/Shaders/Grid.txt` | `src/Editor/LancerEdit/Shaders/Grid.txt` | Header only. |
| `src/OpenLancer.Editor/Shaders/Grid.vert.hlsl` | `src/Editor/LancerEdit/Shaders/Grid.vert.hlsl` | Header only. |
| `src/OpenLancer.Editor/Shaders/Grid.frag.hlsl` | `src/Editor/LancerEdit/Shaders/Grid.frag.hlsl` | A new uniform block: the axis colors, the camera position, the fade distance and the opacity of the fine lines, and no near and far planes. `computeLinearDepth` is removed. The grid fades with the distance from the camera along the plane, not with the depth. The fine lines get their own opacity, and the two levels combine with `max()`, not with a sum, so a line does not get brighter when the levels change. The axis colors come from the editor theme, and the axis lines are as wide as a grid line. |
| `src/OpenLancer.Editor/Scene/ZoneRenderer.cs` | `src/Editor/LancerEdit/GameContent/ZoneRenderer.cs` | An instance class with `IDisposable` that `SceneView` owns, not a static class. The constructor takes the `RenderContext` and a `ResourceManager`. It uses one `ZoneVolumeMaterial` for all draws, not a new material for each draw, because each new material takes a new global material key. It sorts the draws from far to near, because the volumes blend without depth write. `Finish` saves and restores `Wireframe` and the blend mode. The meshes come from `ZoneMeshes`, which builds them in code, not from `DisplayMesh`, which needs SimpleMesh. The unused `SimpleMesh` and `Material` usings and the static `material` field are removed. File-scoped namespace `OpenLancer.Editor.Scene` and nullable annotations. The source file has no header, so OpenLancer adds all three header lines. |
| `src/OpenLancer.Editor/Assets/ThumbnailRenderer.cs` | `src/Editor/LancerEdit/PreviewRenderer.cs` | A port, not a copy (decision D271 of the M23 design). It takes the `Game` and the file system of its resource manager, not `MainWindow`. It renders a `ModelSource` of the edit view, not a loaded `Archetype` or `Asteroid`. It builds a `ResolvedModel` from the paths below the data folder, and adds a `GameObject` of the `ModelResource`. So an unsaved archetype of the active mod gets an image. It sets `EnvMapMaterial` of the model renderer, as the archetype constructor of `GameObject` does. It renders several models at their positions, for a prop group. It runs the world update once and `RenderUpdate` two times, not 120 times. It keeps the factor 1.17 of a sphere file and the camera at 2.35 radii at the angle of the source. The camera distance, the lights and the background color are internal, so the clip preview of the Inspector uses the same values. It clears the `objects` list of the renderer before each image and keeps the comment that names the reason. It puts back the render target and the render state also when a render fails, because the editor draws on after a failed image. No loadout. File-scoped namespace `OpenLancer.Editor.Assets`, nullable annotations and `IDisposable`. The source file has no header, so OpenLancer adds all three header lines. |

## Credits

`GridRender.cs` says that its code is adapted from the "Infinite grid" article on asliceofrendering.com (2020). That page states no license. OpenLancer keeps the credit line in `GridRenderer.cs`. The OpenLancer fragment shader is mostly rewritten. Only the line function and the depth calculation stay close to the original.

## Librelancer license

```
MIT License

Copyright (c) Callum McGing, Librelancer Contributors 2013-2026
Portions Copyright (c) Malte Rupprecht 2011-2012
Portions Copyright (c) Mono.Xna Team 2006

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
