shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear;
uniform float blur_radius = 0.0;
uniform float flash = 0.0;

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 px = SCREEN_PIXEL_SIZE * blur_radius;
    vec4 c = texture(screen_texture, uv) * 0.2;
    c += texture(screen_texture, uv + vec2(px.x, 0.0)) * 0.12;
    c += texture(screen_texture, uv - vec2(px.x, 0.0)) * 0.12;
    c += texture(screen_texture, uv + vec2(0.0, px.y)) * 0.12;
    c += texture(screen_texture, uv - vec2(0.0, px.y)) * 0.12;
    c += texture(screen_texture, uv + px) * 0.08;
    c += texture(screen_texture, uv - px) * 0.08;
    c += texture(screen_texture, uv + vec2(px.x, -px.y)) * 0.08;
    c += texture(screen_texture, uv + vec2(-px.x, px.y)) * 0.08;
    c.rgb += vec3(flash);
    COLOR = c;
}
