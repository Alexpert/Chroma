using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Lighting;
using ChromaTest.Core.Sdl.Lexing;
using ChromaTest.Core.Sdl.Source;
using ChromaTest.Core.Sdl.Syntax;

namespace ChromaTest.Core.Sdl.Binding;

/// <summary>
/// Runs the whole front end: lex, parse, evaluate, bind, and sort the results into a
/// <see cref="Scene"/>.
/// </summary>
public static class SceneBuilder
{
    public static Scene? Build(SourceText source, DiagnosticBag diagnostics)
    {
        IReadOnlyList<Token> tokens = Lexer.Tokenize(source, diagnostics);
        SceneFile file = Parser.Parse(tokens, diagnostics);

        Scope scope = new();
        Evaluator evaluator = new(scope, diagnostics);
        BindingContext context = new(NodeBinderRegistry.CreateDefault(), diagnostics);

        Camera? camera = null;
        List<Light> lights = [];
        List<Solid> roots = [];

        foreach (Statement statement in file.Statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    DefineBinding(let, scope, evaluator, diagnostics);
                    break;

                case ExpressionStatement expression:
                    PlaceSceneItem(expression, evaluator, context, diagnostics, ref camera, lights, roots);
                    break;
            }
        }

        if (camera is null && !diagnostics.HasErrors)
        {
            // Only worth saying when nothing else went wrong: a file that failed to parse
            // has no camera for reasons already explained.
            diagnostics.Error(new SourceSpan(source.Length, 0), "the scene declares no camera");
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
        };
    }

    private static void DefineBinding(
        LetStatement statement,
        Scope scope,
        Evaluator evaluator,
        DiagnosticBag diagnostics)
    {
        if (scope.Contains(statement.Name))
        {
            diagnostics.Error(statement.NameSpan, $"'{statement.Name}' is already defined");
            return;
        }

        SdlValue? value = evaluator.Evaluate(statement.Value);
        if (value is null)
        {
            return;
        }

        // Remember where an object came from, so the hierarchy dump can print
        // 'material=red' rather than the material's components.
        if (value is ObjectValue block)
        {
            value = block.WithSourceName(statement.Name);
        }

        scope.Define(statement.Name, value);
    }

    private static void PlaceSceneItem(
        ExpressionStatement statement,
        Evaluator evaluator,
        BindingContext context,
        DiagnosticBag diagnostics,
        ref Camera? camera,
        List<Light> lights,
        List<Solid> roots)
    {
        SdlValue? value = evaluator.Evaluate(statement.Value);
        if (value is null)
        {
            return;
        }

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
