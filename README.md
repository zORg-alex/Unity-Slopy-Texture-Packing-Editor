# Texture Pack Editor

Open `Tools > Texture Pack Editor`, then select an anchor texture. Textures in the same folder whose
filenames share the anchor's prefix are exposed as detected sampler sources. Additional textures can be
added manually.

The window uses three columns: texture sources and generation settings, tabbed outputs with compact R/G/B/A
stacks, and a tool shelf. Create a recipe asset before generating. Each output channel is an independent,
ordered stack. Drag sampler cards or tool cards onto an expanded stack, between nodes, or directly onto a
collapsed channel header; dropping on a header opens it. Drag nodes or selected node groups to reorder them.
Shift-click selects a range, Ctrl/Cmd-click toggles selection, Ctrl/Cmd-C and Ctrl/Cmd-V copy and paste,
and Delete removes selected nodes. Node settings and the preview beside each node can be collapsed.

Use the `+` output tab to create an output based on any detected map or a custom output. An optional file
name can be set per output; otherwise generation keeps the base texture's file name. Preview pixels and
Levels histograms are processed by one background job at a time after a short edit debounce. Unity texture
capture and preview texture creation remain on the Editor thread.

The output-base role supplies output resolution and Unity importer settings. Generation writes a lossless
RGBA TGA next to that texture using the persistent safe suffix. Source assets are never overwritten.
An existing generated file is replaced only when its importer marker and recipe match; otherwise Unity
chooses a unique filename.

Recipes store detected sources by filename role (the part after the shared prefix), allowing the same stacks
to be applied to another similarly named texture set. Manual sources remain direct asset references.

Desaturate defaults to linear Rec.709 luminance: R 0.2126, G 0.7152, B 0.0722. Green contributes most to
perceived brightness, followed by red and then blue. Its colored weight rails, amount, and two-point output
range are editable. Levels displays the incoming channel histogram with colliding black, midpoint, and white
input handles plus a grayscale two-point output control. Moving either endpoint preserves the midpoint's
proportional position within the remaining range. Thick channel-colored connections appear only between
nodes; partial Desaturate keeps multiple wires, while a full Desaturate produces one scalar wire.

Preview evaluation uses a single forward pass through changed channel stacks; unchanged packed channels are
reused. The actively edited channel is processed first, individual node previews and histogram data appear as
soon as they are ready, and the packed output follows separately. Histograms use one anti-aliased polyline and
slider gradients use cached textures rather than many overlapping IMGUI rectangles.

The left divider resizes the source and preview column. The large preview can show the final output or the
selected node, isolate R/G/B/A, zoom around the cursor with the wheel, and pan by dragging. Zoom changes the
sampled UV region, so the worker renders only the visible portion; `1:1` matches source texels to preview pixels.

Preview jobs are canceled when superseded. Non-selected node results are retained only as 96-pixel thumbnails,
the selected node owns the one full viewport-sized result, and caches are pruned when nodes or output tabs change.
Preview source capture uses compact linear RGBA32 storage; final exported textures keep the high-precision path.
Closing the window destroys all preview, gradient, and editor-chrome textures immediately.
