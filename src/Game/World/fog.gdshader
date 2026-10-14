// Fog of war and day/night tint over the whole world (ADR 0010), one multiplicative pass.
// fog_mask: one texel per tile, R = fog state (0 unknown, 128 explored, 255 visible), G = visual height step (0..3).
// The pixel's tile is the front-most block covering it, as in IsoProjection.PickTile: candidates are the ground picks of
// the pixel moved down by k steps (k = 3..0); the first whose height is at least k wins.
shader_type canvas_item;
render_mode blend_mul, unshaded;

uniform sampler2D fog_mask : filter_nearest, repeat_disable;
uniform int map_size = 512;
uniform vec3 tint = vec3(1.0);
uniform float explored_factor = 0.6;
uniform float unknown_factor = 0.0;

varying vec2 world;

void vertex() {
	world = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

// Ground tile under a world point (IsoProjection.TileAtSubPixel): centre px = 32(x - y) + 32, py = 16(x + y) + 16.
ivec2 tile_at(vec2 p) {
	float u = p.x - 32.0;
	float v = p.y - 16.0;
	return ivec2(floor(vec2(u + 2.0 * v + 32.0, 2.0 * v - u + 32.0) / 64.0));
}

void fragment() {
	float factor = unknown_factor;
	for (int k = 3; k >= 0; k--) {
		ivec2 t = tile_at(world + vec2(0.0, 16.0 * float(k)));
		if (t.x < 0 || t.y < 0 || t.x >= map_size || t.y >= map_size) {
			continue;
		}
		vec2 m = texelFetch(fog_mask, t, 0).rg;
		if (m.g * 255.0 + 0.5 >= float(k)) {
			factor = m.r > 0.75 ? 1.0 : (m.r > 0.25 ? explored_factor : unknown_factor);
			break;
		}
	}
	COLOR = vec4(tint * factor, 1.0);
}
