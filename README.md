# Texture Pack Editor

Samplers include an optional Desaturate section with luminance weights, amount, and range. Full desaturation
produces a scalar; disabled or partial desaturation can retain multiple selected channels and their wires.
The standalone Desaturate tool is removed from the shelf, but existing recipe nodes still work unchanged.
Sample and Constant nodes blend into the incoming stack using Replace, Add, Subtract, Multiply, Screen,
Overlay, Minimum, or Maximum and a 0–1 amount. Defaults preserve replacement behavior. Scalar values
broadcast across channels when mixed with RGB; multi-channel wires remain visible. Add/Subtract retain
unclamped intermediate values for later Levels processing; final packing clamps to 0–1.

Open `Tools > Texture Pack Editor` to use the currently selected texture, Material, or Terrain Layer as the initial input.
Later Project selection changes do not change the editor. Drag another texture, Material, or Terrain Layer into the input
field, or use the refresh button to adopt the current Project selection. Additional textures can be added manually.

Materials supply textures directly from their shader properties. Common Base Color, Normal, Mask/Occlusion,
and Specular properties are recognized, including `_MainTex`, `_BaseMap`, `_BaseColorMap`, `_BumpMap`,
`_NormalMap`, `_MaskMap`, `_OcclusionMap`, and `_SpecGlossMap`. Detail maps are not treated as primary maps.
Use **Material slots** to choose an explicit shader property for each role when using custom shaders or
when multiple properties match. **None** disables a role; **Automatic** restores detection. These choices
apply to the current material input and reset when switching materials. Only assigned project Texture2D
assets become sources; shader defaults, cubemaps, and texture arrays are not exported.

Empty Material and Terrain Layer slots can be generated when a Basemap is assigned. Add an output for the
empty role and use its **Resolution** popup to choose a square power-of-two size, up to the shorter side of
the base map. The default is the largest allowed size. The chosen size is saved in the recipe and captured
when generation starts. Empty mask outputs start with neutral RGBA values (0, 1, 1, 0); empty normals start
flat. Existing sampler nodes referencing the empty role use the same neutral input. Mask outputs import
as linear data, and normal outputs as normal maps. Generated empty-slot results remember their neutral
origin, so regeneration does not compound earlier edits. Their resolution remains editable on later runs.

A new unsaved material recipe starts with one output for each detected role. Generation assigns each result
to that role's captured shader property, preserving texture scale, offset, and other material settings.
When filling an empty standard texture slot, its texture keyword is enabled if the shader declares one;
existing populated slots retain their keyword settings.
Existing generated assignments are overwritten in place, with original source recovery just like Terrain Layers.
The material, shader, property names, and existing assignments are captured at the start of the job. You can
move on to another material or Terrain Layer while it runs; the completed textures still apply to the original
target. If that target's shader or texture assignment is edited during generation, the file is saved but the
concurrent edit is not replaced. Texture assignment supports Undo, and completion does not select or ping assets.

Terrain Layers supply Basemap, Normal, and MaskMap directly from their diffuse, normal, and mask slots,
regardless of filenames or folders. A new unsaved recipe starts with one output for each assigned slot;
loading an existing recipe preserves its stacks. Save the recipe, edit the outputs, then generate to assign
the resulting textures back to the selected layer. The output's Base role determines its target slot.
Only generated slots are changed; tiling, remapping, normal scale, and other layer settings are preserved.
Layer assignment changes support Undo. A layer shared by multiple terrains updates all those terrains.

If a slot already contains a Texture Pack Editor result, generation overwrites that same file and preserves
its GUID, even with a different recipe or suffix. Its File name field is disabled while reusing that assignment.
The source textures remain intact. New exports remember the original input GUID, allowing previews and
regeneration to use the original instead of compounding previous edits. Older exports fall back to locating
the unsuffixed original in the same folder; if it cannot be found, the assigned generated texture is the input.
Normal-map capture decodes Unity's packed representation back to RGB before processing and reimporting.
Only one output per terrain slot is allowed; a warning directs you to conflicting tabs. The target layer and
its slot assignments are captured when generation starts, so switching inputs cannot redirect the results.

The window uses three columns: texture sources and generation settings, tabbed outputs with compact R/G/B/A
stacks, and a tool shelf. Create a recipe asset before generating. Each output channel is an independent,
ordered stack. Drag sampler cards or tool cards onto an expanded stack, between nodes, or directly onto a
collapsed channel header; dropping on a header opens it. Drag nodes or selected node groups to reorder them.
Shift-click selects a range, Ctrl/Cmd-click toggles selection, Ctrl/Cmd-C and Ctrl/Cmd-V copy and paste,
and Delete removes selected nodes. Node settings and the preview beside each node can be collapsed.

Use the `+` output tab to create an output based on any configured semantic role or a custom output. An optional file
name can be set per output; otherwise generation keeps the base texture's file name. Preview pixels and
Levels histograms are processed by one background job at a time after a short edit debounce. Unity texture
capture and preview texture creation remain on the Editor thread.

The output-base role supplies output resolution and Unity importer settings. Generation writes a lossless
RGBA TGA next to that texture using the persistent safe suffix. Source assets are never overwritten.
In texture input mode, an existing generated file is replaced only when its importer marker, recipe, and output ID match. Before
generation, conflicting tab names and existing files trigger a warning with proposed unique filenames.
Choose **Use unique names** to save those names to the recipe, **Edit filenames** to return to the conflicting
tab, or **Cancel**. Older exports with recipe-only ownership markers also require resolving this warning in
texture input mode; a generated texture assigned to the selected Terrain Layer is authorized for replacement.
Regeneration replaces only the image file at that path; its existing Unity `.meta`
file and GUID remain in place, so materials and other asset references continue pointing at the result.

Full-resolution channel evaluation and TGA writing run as a cancellable background job. A progress bar at
the top of the window reports source capture, packing, writing, and import stages. The active recipe, anchor,
suffix, and output stacks are snapshotted when generation starts, so the rest of the editor remains available
for preparing another texture while the current job finishes. Source assignments are captured as well;
deleting or rearranging tabs cannot redirect completion to another tab. Unity texture capture and final asset import
remain on the Editor thread because Unity does not expose those operations as thread-safe APIs.
Packing is distributed across roughly three quarters of the available logical processors so the Editor remains
responsive. Prepared sampler nodes read captured pixel buffers directly instead of hashing an ID per pixel.

Detected textures are assigned semantic roles such as Basemap, MaskMap, Normal, and Specular. Source cards are
shown in project-role order as `textureName [Role]`. The settings button beside the anchor edits only the
project-wide role catalog; the matching button beside the recipe edits only that recipe's overrides. Literal rules are case-insensitive `|`-separated
contained terms; advanced roles can use regular expressions. Longest matches win, while tied matches and
multiple textures assigned to one role remain unresolved until their rules are corrected.
Both settings panels are compact single-instance dropdowns and close when focus moves elsewhere.

Recipes store stable semantic role IDs rather than texture naming conventions. A recipe can add terms to a
project role or override its rule entirely. Changing texture families therefore never rewrites the recipe.
Generated outputs are excluded from source detection, and selecting an owned suffixed output resolves back to
its original texture family. Manual sampler sources remain direct asset references.

Trailing resolution tags such as `_2k`, `_4k`, and `_8K` are ignored when detecting families and matching roles.
For example, `Rock_BaseColor_4k` and `Rock_Normal_4k` belong to the `Rock` family. Original asset and output
filenames keep their resolution tags. Multiple resolutions of the same role in one folder remain an explicit
conflict rather than choosing one automatically.

Desaturate defaults to linear Rec.709 luminance: R 0.2126, G 0.7152, B 0.0722. Green contributes most to
perceived brightness, followed by red and then blue. Its colored weight rails, amount, and two-point output
range are editable. Levels displays the incoming channel histogram with colliding black, midpoint, and white
input handles plus a grayscale two-point output control. Moving either endpoint preserves the midpoint's
proportional position within the remaining range. Thick channel-colored connections appear only between
nodes; partial Desaturate keeps multiple wires, while a full Desaturate produces one scalar wire.
Node bodies animate when folded. A collapsed node keeps the rounded lower edge of its container visible, and
dragging tools, samplers, single nodes, or node groups shows a translucent label ghost beside the pointer.

Preview evaluation uses a single forward pass through changed channel stacks; unchanged packed channels are
reused. The actively edited channel is processed first, individual node previews and histogram data appear as
soon as they are ready, and the packed output follows separately. Histograms use one anti-aliased polyline and
slider gradients use cached textures rather than many overlapping IMGUI rectangles.

The left divider resizes the source and preview column. The large preview can show the final output or the
selected node, isolate R/G/B/A, zoom around the cursor with the wheel, and pan by dragging. Zoom changes the
sampled UV region, so the worker renders only the visible portion; `1:1` matches source texels to preview pixels.

Preview jobs are canceled when superseded. Canceled jobs carry their unfinished channel changes into the
next preview job. Procedural noise uses the same UV region as texture sampling, so zoom and pan preserve its
position relative to the texture. Non-selected node results are retained only as 96-pixel thumbnails,
the selected node owns the one full viewport-sized result, and caches are pruned when nodes or output tabs change.
Preview and export source capture use compact linear RGBA32 storage, matching the 8-bit-per-channel TGA output
while avoiding the previous RGBAFloat memory spike. Closing the window cancels active work and destroys all
preview, gradient, and editor-chrome textures immediately.

### Height reconstruction

The Height tool reads its own full RGB texture and outputs a scalar; sampler channel wires remain available. Normal Integration reconstructs relative height with a Fourier gradient solve. Choose Seamless for tiling textures or disable it for mirrored boundaries, and flip normal Y for the opposite normal convention. Strength (including negative values), Center and source blending control the result.

Solve resolution caps the longest source dimension (128–2048). Preview and generation use the same full-image solve before cropping/resampling, so zooming does not change the height. Results are cached with source dependency hashes in a bounded 64 MB cache. Flat normals produce mid-gray. Absolute depth and missing large-scale shape cannot be recovered from normals. Current output remains 8-bit TGA.
