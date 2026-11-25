shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled;

uniform vec4 radiation_color : source_color = vec4(0.35, 1.0, 0.22, 0.75);
uniform float pulse_speed = 2.2;

void fragment() {
    float wave = 0.72 + 0.28 * sin(TIME * pulse_speed + UV.x * 24.0 + UV.y * 18.0);
    ALBEDO = radiation_color.rgb;
    EMISSION = radiation_color.rgb * (1.4 + wave * 1.1);
    ALPHA = radiation_color.a * wave;
}
