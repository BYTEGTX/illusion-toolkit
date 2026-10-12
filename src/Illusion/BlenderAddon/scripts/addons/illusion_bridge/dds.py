"""DDS post-load fixes for game textures."""

import numpy as np


def unswizzle_dxt5nm(image):
    """Rebuild a plain tangent normal map from the game's two-channel one, in place.

    The game keeps a normal's x and y only and stores them two ways: a map with an alpha of its own
    (DXT5) has x in A and y in G; a map without one (DXT1 - every ground texture: roads, pavements,
    kerbs) has x in R and y in G, and its blue is the gloss mask. Reading the second kind as the first
    takes x from an alpha that is 1 everywhere: the normal then lies IN the surface, and a path tracer
    draws the road black. So x comes from the alpha only where the picture has one.

    The result is packed into the .blend: a renderer reads an unpacked picture from its file, and the
    file still holds the game's layout (blue low = a normal pointing into the surface - black again).

    Idempotent: the image is tagged with a custom property after processing so
    a cached (check_existing) load is never unswizzled twice.
    """
    if image.get("illusion_dxt5nm"):
        return
    n = len(image.pixels)
    if n == 0 or n % 4 != 0:
        return  # failed load or non-RGBA layout; leave untouched

    buf = np.empty(n, dtype=np.float32)
    image.pixels.foreach_get(buf)
    rgba = buf.reshape(-1, 4)

    has_alpha = float(rgba[:, 3].min()) < 0.995
    x = (rgba[:, 3] if has_alpha else rgba[:, 0]) * 2.0 - 1.0
    y = rgba[:, 1] * 2.0 - 1.0
    z = np.sqrt(np.clip(1.0 - x * x - y * y, 0.0, 1.0))

    rgba[:, 0] = (x + 1.0) * 0.5
    rgba[:, 1] = (y + 1.0) * 0.5
    rgba[:, 2] = (z + 1.0) * 0.5
    rgba[:, 3] = 1.0

    image.pixels.foreach_set(buf)
    image.update()
    try:
        image.pack()
    except RuntimeError:
        pass  # nothing to pack (no buffer): the picture stays as the viewport shows it
    image["illusion_dxt5nm"] = True
