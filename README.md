# Texture Pack Editor

Open `Tools > Texture Pack Editor`, then select an anchor texture. Textures in the same folder whose
filenames share the anchor's prefix are exposed as detected sampler sources. Additional textures can be
added manually.

Create a recipe asset before generating. Each output R/G/B/A channel is an independent, ordered stack.
Drag sampler cards or toolbar actions into a stack; drag nodes or selected node groups to reorder them.
Shift-click selects a range, Ctrl/Cmd-click toggles selection, Ctrl/Cmd-C and Ctrl/Cmd-V copy and paste,
and Delete removes selected nodes. Node settings and the preview beside each node can be collapsed.

The output-base role supplies output resolution and Unity importer settings. Generation writes a lossless
RGBA TGA next to that texture using the persistent safe suffix. Source assets are never overwritten.
An existing generated file is replaced only when its importer marker and recipe match; otherwise Unity
chooses a unique filename.

Recipes store detected sources by filename role (the part after the shared prefix), allowing the same stacks
to be applied to another similarly named texture set. Manual sources remain direct asset references.

Desaturate defaults to linear Rec.709 luminance: R 0.2126, G 0.7152, B 0.0722. Green contributes most to
perceived brightness, followed by red and then blue. The weights are editable and normalized by default.
