// Convergence pass: how much noise is left in the accumulated image.
//
// Every pixel of a path tracer is a Monte Carlo estimate, so it comes with an error bar. The
// trace keeps the running mean of the radiance in RGB and the running mean of the squared
// luminance in alpha, which is exactly what a variance needs:
//
//     Var    = (E[L^2] - E[L]^2) * N/(N-1)   the spread of the samples themselves
//     stdErr = sqrt(Var / N) = sqrt((E[L^2] - E[L]^2) / (N-1))
//                                            the spread of their *mean*, which is shown
//
// The standard error is what falls as 1/sqrt(N) and what the viewer perceives as the image
// settling down. Dividing it by the mean makes it comparable between a dim corner and a lit
// wall, and turns it into the percentage the HUD prints.
//
// This pass writes one value per pixel; the reduction to a single number is done by the
// mipmap chain, on the CPU side (see ConvergenceMeter).
#version 330 core

in vec2 vNdc;
out vec4 FragColor;

uniform sampler2D uAccumulation;
uniform int       uSampleCount;

// Below this luminance a relative error means nothing: the denominator is noise itself, and
// an unlit background would swamp the average with enormous meaningless ratios.
const float MIN_LUMINANCE = 1e-3;

void main()
{
    vec4  accum = texture(uAccumulation, vNdc * 0.5 + 0.5);
    float mean  = dot(accum.rgb, vec3(0.2126, 0.7152, 0.0722));

    // max() against rounding: E[L^2] and E[L]^2 are two large, nearly equal numbers late in a
    // render, and their difference can land just below zero.
    float variance = max(accum.a - mean * mean, 0.0);

    // Dividing by N-1 rather than N is Bessel's correction and the division by N that turns a
    // variance into a standard error, folded together. It matters early on, where the biased
    // form would flatter a two-sample image; by the time N is large the two agree.
    float stdError = sqrt(variance / float(max(uSampleCount - 1, 1)));

    // R carries the error, G carries whether this pixel counted. The mipmap chain averages
    // both, so R/G is the mean over the lit pixels only -- weighting that a plain average of
    // R could not express.
    float weight = mean > MIN_LUMINANCE ? 1.0 : 0.0;

    FragColor = vec4(stdError / max(mean, MIN_LUMINANCE) * weight, weight, 0.0, 1.0);
}
