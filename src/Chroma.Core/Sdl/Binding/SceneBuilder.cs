using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Lighting;
using Chroma.Core.Sdl.Lexing;
using Chroma.Core.Sdl.Source;
using Chroma.Core.Sdl.Syntax;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Runs the whole front end: lex, parse, evaluate, bind, and sort the results into a
/// <see cref="Scene"/>.
/// </summary>
/// <remarks>
/// The top level of a file is a statement list, and so is the inside of a block. This walks
/// the same <see cref="Evaluator.Execute"/> a block does and then sorts what comes out —
/// which is why a <c>for</c> loop needs nothing here: it produces entries, and entries are
/// what this already knows how to place.
/// </remarks>
public static class SceneBuilder
{
    public static Scene? Build(SourceText source, DiagnosticBag diagnostics)
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(source, diagnostics);
        SceneFile file = Parser.Parse(tokens, diagnostics);

        Evaluator evaluator = new(diagnostics);
        List<BoundEntry> entries = [];
        evaluator.Execute(file.Statements, new Scope(), entries);

        BindingContext context = new(NodeBinderRegistry.CreateDefault(), diagnostics);

        Camera? camera = null;
        RenderSettings? render = null;
        List<Light> lights = [];
        List<Solid> roots = [];

        foreach (BoundEntry entry in entries)
        {
            switch (entry)
            {
                case BoundChild child:
                    PlaceSceneItem(
                        child.Value, context, diagnostics,
                        ref camera, ref render, lights, roots);
                    break;

                case BoundField field:
                    // 'name: value' parses anywhere a statement does, and means something
                    // only inside a block. Saying so beats the parse error it used to be.
                    diagnostics.Error(
                        field.NameSpan,
                        $"'{field.Name}:' is a field and belongs inside a block, "
                        + "not at the top level of a scene");
                    break;
            }
        }

        if (camera is null && !diagnostics.HasErrors)
        {
            // Only worth saying when nothing else went wrong: a file that failed to parse
            // has no camera for reasons already explained.
            diagnostics.Error(
                new SourceSpan(source.Length, 0, source), "the scene declares no camera");
        }

        if (diagnostics.HasErrors || camera is null)
        {
            return null;
        }

        return new Scene
        {
            Camera = camera,
            Lights = lights,
            Roots = roots,

            // Unlike the camera, a missing render block is not an error: every setting has
            // a usable default, and most scenes have no reason to say anything about them.
            Render = render ?? RenderSettings.Default,
        };
    }

    private static void PlaceSceneItem(
        SdlValue value,
        BindingContext context,
        DiagnosticBag diagnostics,
        ref Camera? camera,
        ref RenderSettings? render,
        List<Light> lights,
        List<Solid> roots)
    {
        switch (context.Bind(value))
        {
            case null:
                break;

            case Camera bound:
                if (camera is not null)
                {
                    diagnostics.Error(value.Span, "a scene may declare only one camera");
                }
                else
                {
                    camera = bound;
                }

                break;

            case RenderSettings bound:
                if (render is not null)
                {
                    diagnostics.Error(value.Span, "a scene may declare only one render block");
                }
                else
                {
                    render = bound;
                }

                break;

            case Light light:
                lights.Add(light);
                break;

            case Solid solid:
                roots.Add(solid);
                break;

            default:
                diagnostics.Error(
                    value.Span,
                    $"{value.Describe()} cannot appear on its own at the top level of a scene");
                break;
        }
    }
}
