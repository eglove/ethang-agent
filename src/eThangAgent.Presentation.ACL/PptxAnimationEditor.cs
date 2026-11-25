using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using P = DocumentFormat.OpenXml.Presentation;

// S3220/S3878 flag every OOXML element-constructor call below: each element tree is
// built by handing its children to the constructor, which is the overload the SDK
// declares for it.
#pragma warning disable S3220, S3878
namespace eThangAgent.Presentation.ACL;

/// <summary>An entrance effect the animation vocabulary can add: the name an operation
///     sets it by, the preset PowerPoint writes for it, and the filter its behaviour
///     animates with. A null filter is an effect that only makes the shape visible.</summary>
/// <param name="Name">The name an operation sets the effect to.</param>
/// <param name="PresetId">The presetID PowerPoint writes for the effect.</param>
/// <param name="Filter">The animEffect filter, or null for a visibility-only effect.</param>
internal sealed record PptxEntranceEffect(string Name, int PresetId, string? Filter);

/// <summary>The effect and the shape one operation names, either of which may be absent.
///     </summary>
/// <param name="Effect">The effect to set, or null to leave it unchanged.</param>
/// <param name="Address">The shape the operation names, or null when it names none.</param>
internal sealed record PptxEffectRequest(PptxEntranceEffect? Effect, PptxAddress? Address);

/// <summary>One animation change resolved against the open package: the 1-based effect it
///     acts on, the effect, trigger and duration it sets, and the shape an added effect
///     targets. A null member is left as it is.</summary>
/// <param name="Index">The 1-based effect index the change acts on.</param>
/// <param name="Effect">The effect to set, or null to leave it unchanged.</param>
/// <param name="Trigger">The trigger to set, or null to leave it unchanged.</param>
/// <param name="DurationMilliseconds">The duration to set, or null to leave it unchanged.</param>
/// <param name="TargetShapeId">The drawing id of the shape an added effect animates.</param>
internal sealed record PptxAnimationChange(int Index, PptxEntranceEffect? Effect, string? Trigger,
    int? DurationMilliseconds, uint TargetShapeId);

/// <summary>The animation edits: retiming, retriggering and replacing an existing effect,
///     removing one, and appending a simple entrance effect to a shape. Effects live in
///     the slide part's timing tree, so every change is a rewrite of that tree - an effect
///     is one timing node carrying a preset, and playback order is the order those nodes
///     appear in. Anything the vocabulary cannot express faithfully - a motion path, a
///     trigger sequence, a chain of several effects - is refused rather than approximated,
///     and removing the last effect removes the timing element with it.</summary>
internal static class PptxAnimationEditor
{
  /// <summary>The sentence every refusal states about what is supported.</summary>
  internal const string Supported = "only reorder, remove, retime and simple entrance effects are supported";

  /// <summary>The character separators a name uses to chain several effects together.
  ///     </summary>
  private static readonly char[] ChainSeparators = [',', ';', '+'];

  /// <summary>The trigger an effect waits for a click.</summary>
  private const string TriggerOnClick = "onClick";

  /// <summary>The trigger an effect starts with the one before it.</summary>
  private const string TriggerWithPrevious = "withPrevious";

  /// <summary>The trigger an effect starts when the one before it finishes.</summary>
  private const string TriggerAfterPrevious = "afterPrevious";

  /// <summary>The delay a start condition carries when it waits for the presenter.
  ///     </summary>
  private const string Indefinite = "indefinite";

  /// <summary>The entrance effects a new effect can be and a replacement can name: the
  ///     presets PowerPoint writes for them, and the filter each animates with.</summary>
  private static readonly PptxEntranceEffect[] EntranceEffects =
  [
    new("appear", 1, null),
    new("fade", 10, "fade"),
    new("wipe", 22, "wipe(down)"),
  ];

  /// <summary>Resolves one of the two animation operations, or null when the operation
  ///     belongs to another task.</summary>
  /// <param name="slides">The deck's slides in presentation order.</param>
  /// <param name="operation">The operation to resolve.</param>
  internal static Result<PptxResolvedEdit>? Resolve(IReadOnlyList<SlidePart> slides, PptxOperation operation)
    => operation switch
    {
      SetAnimation set => ResolveSetAnimation(slides, set),
      RemoveAnimation remove => ResolveRemoveAnimation(slides, remove),
      _ => null,
    };

  /// <summary>The slide's animation effects in playback order: every timing node that
  ///     carries a preset, which is the same walk a read reports.</summary>
  /// <param name="slidePart">The slide part whose timing tree is read.</param>
  internal static List<P.CommonTimeNode> Effects(SlidePart slidePart)
  {
    ArgumentNullException.ThrowIfNull(slidePart);
    P.TimeNodeList? list = slidePart.Slide?.Timing?.TimeNodeList;
    return list is null ? [] : [.. list.Descendants<P.CommonTimeNode>().Where(PptxShapeWalker.IsEffect)];
  }

  private static Result<PptxResolvedEdit> ResolveSetAnimation(IReadOnlyList<SlidePart> slides, SetAnimation operation)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Slide);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    SlidePart slidePart = slide.Value;
    int count = Effects(slidePart).Count;
    if (operation.EffectIndex < 1 || operation.EffectIndex > count + 1)
    {
      return Failed(PptxErrorCodes.AnimationNotFound, Missing(operation.Slide, operation.EffectIndex, count));
    }

    Result<PptxAnimationChange> change = Change(slidePart, operation);
    if (!change.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(change.Error);
    }

    PptxAnimationChange resolved = change.Value;
    return Result.Success(new PptxResolvedEdit(Key(operation.Slide, operation.EffectIndex),
        () => Apply(slidePart, resolved)));
  }

  private static Result<PptxResolvedEdit> ResolveRemoveAnimation(IReadOnlyList<SlidePart> slides, RemoveAnimation operation)
  {
    Result<SlidePart> slide = SlideAt(slides, operation.Slide);
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxResolvedEdit>(slide.Error);
    }

    SlidePart slidePart = slide.Value;
    int count = Effects(slidePart).Count;
    if (operation.EffectIndex < 1 || operation.EffectIndex > count)
    {
      return Failed(PptxErrorCodes.AnimationNotFound, Missing(operation.Slide, operation.EffectIndex, count - 1));
    }

    int index = operation.EffectIndex;
    return Result.Success(new PptxResolvedEdit(Key(operation.Slide, index), () => RemoveEffect(slidePart, index)));
  }

  /// <summary>The change an operation asks for, validated before anything mutates: the
  ///     effect name, the trigger and the duration are each checked, and an added effect
  ///     must name a shape the slide has.</summary>
  private static Result<PptxAnimationChange> Change(SlidePart slidePart, SetAnimation operation)
  {
    Result<PptxEffectRequest> request = Request(operation.Effect);
    if (!request.IsSuccess)
    {
      return Result.Failure<PptxAnimationChange>(request.Error);
    }

    Result<string> trigger = Trigger(operation.Trigger);
    if (!trigger.IsSuccess)
    {
      return Result.Failure<PptxAnimationChange>(trigger.Error);
    }

    if (operation.DurationMilliseconds is < 0)
    {
      return Failure<PptxAnimationChange>(PptxErrorCodes.InvalidAddress,
          $"A duration of {operation.DurationMilliseconds} milliseconds is not a duration; a retime takes a whole number of milliseconds, zero or more.");
    }

    List<P.CommonTimeNode> effects = Effects(slidePart);
    bool adds = operation.EffectIndex == effects.Count + 1;
    Result<uint> target = Target(slidePart, request.Value, adds, effects, operation.EffectIndex);
    return target.IsSuccess
        ? Result.Success(new PptxAnimationChange(operation.EffectIndex, request.Value.Effect, trigger.Value,
            operation.DurationMilliseconds, target.Value))
        : Result.Failure<PptxAnimationChange>(target.Error);
  }

  /// <summary>The drawing id of the shape a change acts on: an added effect must name a
  ///     shape the slide has, and a change to an existing effect may name only the shape
  ///     that effect already animates.</summary>
  private static Result<uint> Target(SlidePart slidePart, PptxEffectRequest request, bool adds,
      List<P.CommonTimeNode> effects, int index)
  {
    if (adds)
    {
      return request.Address is null
          ? Failure<uint>(PptxErrorCodes.InvalidAddress,
              "An added effect must name the shape it animates: set the effect to '<effect> <address>', for example 'fade s1.sh2'.")
          : ShapeId(slidePart, request.Address);
    }

    if (request.Address is null)
    {
      return Result.Success(0U);
    }

    Result<uint> named = ShapeId(slidePart, request.Address);
    if (!named.IsSuccess)
    {
      return named;
    }

    uint current = ShapeIdOf(effects[index - 1]);
    return named.Value == current
        ? named
        : Failure<uint>(PptxErrorCodes.OperationUnsupported,
            $"Effect {index} animates a different shape than '{request.Address.Render()}'; moving an effect onto another shape is not supported, so " + Supported + ".");
  }

  /// <summary>The drawing id of the shape an address names, or AddressNotFound when the
  ///     slide has no such shape.</summary>
  private static Result<uint> ShapeId(SlidePart slidePart, PptxAddress address)
  {
    OpenXmlElement? element = PptxShapeWalker.ElementAt(slidePart, address.ShapePath);
    uint? id = element?.Descendants<P.NonVisualDrawingProperties>().FirstOrDefault()?.Id?.Value;
    return id is { } value
        ? Result.Success(value)
        : Failure<uint>(PptxErrorCodes.AddressNotFound,
            $"Address '{address.Render()}' resolves to no shape on slide {address.Slide}.");
  }

  /// <summary>The drawing id an effect animates, or 0 when its target names no shape.
  ///     </summary>
  private static uint ShapeIdOf(P.CommonTimeNode effect)
  {
    string? text = effect.Descendants<P.ShapeTarget>().FirstOrDefault()?.ShapeId?.Value;
    return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) ? id : 0U;
  }

  /// <summary>The trigger an operation sets, or the empty string when it sets none. A
  ///     trigger outside the three PowerPoint writes for a main sequence is a trigger
  ///     sequence, which is refused.</summary>
  private static Result<string> Trigger(string? trigger)
    => trigger switch
    {
      null => Result.Success(string.Empty),
      TriggerOnClick or TriggerWithPrevious or TriggerAfterPrevious => Result.Success(trigger),
      _ => Unsupported<string>($"'{trigger}' is a trigger sequence; " + Supported + "."),
    };

  /// <summary>The effect and shape an operation names. A name outside the vocabulary, a
  ///     motion path, and a chain of several effects are each refused with the message
  ///     stating what is supported.</summary>
  private static Result<PptxEffectRequest> Request(string? name)
  {
    if (name is null)
    {
      return Result.Success(new PptxEffectRequest(null, null));
    }

    Result<string> allowed = Allowed(name);
    if (!allowed.IsSuccess)
    {
      return Result.Failure<PptxEffectRequest>(allowed.Error);
    }

    PptxEntranceEffect? effect = null;
    PptxAddress? address = null;
    foreach (string token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
      Result<PptxEffectRequest> named = Named(token, effect, address);
      if (!named.IsSuccess)
      {
        return named;
      }

      effect = named.Value.Effect;
      address = named.Value.Address;
    }

    return Result.Success(new PptxEffectRequest(effect, address));
  }

  /// <summary>One token of a name read as an effect or an address. A token that is
  ///     neither, or a second one of a kind the name already carried, is refused.
  ///     </summary>
  private static Result<PptxEffectRequest> Named(string token, PptxEntranceEffect? effect, PptxAddress? address)
  {
    if (Entrance(token) is { } found)
    {
      return effect is null
          ? Result.Success(new PptxEffectRequest(found, address))
          : Chain(token);
    }

    Result<PptxAddress> parsed = PptxAddress.Parse(token);
    return parsed.IsSuccess && parsed.Value.Kind == PptxAddressKind.Shape
        ? Addressed(token, parsed.Value, effect, address)
        : Unsupported<PptxEffectRequest>($"'{token}' is not an effect or a shape address; " + Supported + ".");
  }

  /// <summary>A shape address read as the target of a request that may already carry one.
  ///     </summary>
  private static Result<PptxEffectRequest> Addressed(string token, PptxAddress parsed,
      PptxEntranceEffect? effect, PptxAddress? address)
    => address is null
        ? Result.Success(new PptxEffectRequest(effect, parsed))
        : Chain(token);

  /// <summary>The entrance effect a name token stands for: the vocabulary's own name, or
  ///     the preset class and id a read reports. A preset outside the vocabulary names an
  ///     effect this build cannot write.</summary>
  private static PptxEntranceEffect? Entrance(string token)
  {
    PptxEntranceEffect? named = Array.Find(EntranceEffects, effect =>
        string.Equals(effect.Name, token, StringComparison.OrdinalIgnoreCase));
    return named is null && token.StartsWith("entr:", StringComparison.OrdinalIgnoreCase)
        ? ByPreset(token)
        : named;
  }

  /// <summary>The entrance effect a preset class and id name, or null when the vocabulary
  ///     cannot write that preset.</summary>
  private static PptxEntranceEffect? ByPreset(string token)
    => int.TryParse(token[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int preset)
        ? Array.Find(EntranceEffects, effect => effect.PresetId == preset)
        : null;

  /// <summary>Whether a name states something the vocabulary refuses before it is read
  ///     as an effect: a motion path, or several effects chained together.</summary>
  private static Result<string> Allowed(string name) => name switch
  {
    _ when name.Contains("motion", StringComparison.OrdinalIgnoreCase)
        || name.Contains("path", StringComparison.OrdinalIgnoreCase) =>
        Unsupported<string>($"'{name}' names a motion path; " + Supported + "."),
    _ when name.IndexOfAny(ChainSeparators) >= 0 || name.Contains("then", StringComparison.OrdinalIgnoreCase) =>
        Unsupported<string>($"'{name}' names more than one effect; " + Supported + "."),
    _ => Result.Success(name),
  };

  private static Result<PptxEffectRequest> Chain(string token)
    => Unsupported<PptxEffectRequest>($"'{token}' is a second effect in one request; " + Supported + ".");

  /// <summary>Applies a resolved change to the slide's timing tree: an effect that exists
  ///     is rewritten, and one past the last effect is appended as a new click step.
  ///     </summary>
  private static void Apply(SlidePart slidePart, PptxAnimationChange change)
  {
    List<P.CommonTimeNode> effects = Effects(slidePart);
    if (change.Index <= effects.Count)
    {
      Rewrite(effects, change);
    }
    else
    {
      Append(slidePart, change);
    }

    slidePart.Slide?.Save();
  }

  /// <summary>Rewrites one existing effect: its preset, its trigger and its duration,
  ///     leaving every member the change does not name as it was.</summary>
  private static void Rewrite(List<P.CommonTimeNode> effects, PptxAnimationChange change)
  {
    P.CommonTimeNode node = effects[change.Index - 1];
    if (change.Effect is { } effect)
    {
      node.PresetId = effect.PresetId;
      node.PresetClass = P.TimeNodePresetClassValues.Entrance;
      node.PresetSubtype = 0;
    }

    if (change.DurationMilliseconds is { } milliseconds)
    {
      Retime(node, milliseconds);
    }

    if (change.Trigger is { Length: > 0 } trigger)
    {
      WriteTrigger(node, trigger, effects, change.Index);
    }
  }

  /// <summary>Sets an effect's duration on its own node and on every behaviour below it,
  ///     so the effect and what it animates run for the same time. A visibility behaviour
  ///     keeps its one-millisecond duration, which is what PowerPoint writes for it.
  ///     </summary>
  private static void Retime(P.CommonTimeNode node, int milliseconds)
  {
    string duration = Text(milliseconds);
    node.Duration = duration;
    foreach (P.CommonTimeNode child in node.Descendants<P.CommonTimeNode>().Where(child =>
        !child.Ancestors<P.SetBehavior>().Any()))
    {
      child.Duration = duration;
    }
  }

  /// <summary>Writes an effect's trigger. The click lives on the nearest enclosing timing
  ///     node that waits for the presenter, and an effect that follows the previous one
  ///     states that delay on its own node, which is where a read finds it.</summary>
  private static void WriteTrigger(P.CommonTimeNode node, string trigger,
      List<P.CommonTimeNode> effects, int index)
  {
    P.Condition own = OwnCondition(node);
    P.Condition group = ClickCondition(node);
    string afterDelay = AfterPreviousDelay(effects, index, node);
    own.Event = null;
    node.NodeType = NodeType(trigger);
    if (ReferenceEquals(group, own))
    {
      own.Delay = SharedDelay(trigger, afterDelay);
      return;
    }

    own.Delay = trigger == TriggerAfterPrevious ? afterDelay : "0";
    group.Event = null;
    group.Delay = trigger == TriggerOnClick ? Indefinite : "0";
  }

  /// <summary>The delay an effect that follows the previous one waits: the previous
  ///     effect's duration, or the effect's own duration when it is the first one.
  ///     </summary>
  private static string AfterPreviousDelay(List<P.CommonTimeNode> effects, int index, P.CommonTimeNode node)
  {
    P.CommonTimeNode? previous = index > 1 && index - 2 < effects.Count ? effects[index - 2] : null;
    return Milliseconds(previous?.Duration?.Value) ?? Milliseconds(node.Duration?.Value) ?? "0";
  }

  /// <summary>The delay a start condition carries when it waits for a click, or when it
  ///     runs with the previous effect.</summary>
  private static string GroupDelay(string trigger) => trigger == TriggerOnClick ? Indefinite : "0";

  /// <summary>The delay a start condition carries when the effect node and its click
  ///     group are the same node.</summary>
  private static string SharedDelay(string trigger, string afterDelay)
    => trigger == TriggerAfterPrevious ? afterDelay : GroupDelay(trigger);

  /// <summary>The node type PowerPoint writes for a trigger.</summary>
  private static P.TimeNodeValues NodeType(string trigger) => trigger switch
  {
    TriggerWithPrevious => P.TimeNodeValues.WithEffect,
    TriggerAfterPrevious => P.TimeNodeValues.AfterEffect,
    _ => P.TimeNodeValues.ClickEffect,
  };

  /// <summary>An effect's own start condition, created when it has none.</summary>
  private static P.Condition OwnCondition(P.CommonTimeNode node)
    => node.StartConditionList?.Elements<P.Condition>().FirstOrDefault() ?? AddCondition(node);

  /// <summary>The start condition of the nearest enclosing timing node that waits for the
  ///     presenter, or the effect's own condition when no enclosing node does.</summary>
  private static P.Condition ClickCondition(P.CommonTimeNode node)
    => node.Ancestors<P.CommonTimeNode>()
        .Select(parent => parent.StartConditionList?.Elements<P.Condition>().FirstOrDefault())
        .FirstOrDefault(condition => condition is not null && PptxShapeWalker.StartsOnAClick(condition))
        ?? OwnCondition(node);

  private static P.Condition AddCondition(P.CommonTimeNode node)
  {
    P.Condition condition = new() { Delay = "0" };
    node.StartConditionList = new P.StartConditionList(condition);
    return condition;
  }

  /// <summary>Appends a simple entrance effect as a new click step at the end of the
  ///     slide's main sequence, creating the timing element when the slide has none.
  ///     </summary>
  private static void Append(SlidePart slidePart, PptxAnimationChange change)
  {
    P.Slide slide = slidePart.Slide ?? throw new InvalidOperationException("the slide part carries no slide");
    P.TimeNodeList list = slide.Timing?.TimeNodeList ?? NewTiming(slide);
    P.CommonTimeNode main = list.Descendants<P.SequenceTimeNode>().FirstOrDefault()?.CommonTimeNode
        ?? throw new InvalidOperationException("the slide's timing carries no main sequence");
    uint next = NextId(list);
    P.ChildTimeNodeList children = main.ChildTimeNodeList ?? main.AppendChild(new P.ChildTimeNodeList());
    children.Append(ClickStep(change, next));
  }

  /// <summary>A click step holding one effect: the node PowerPoint waits on, the step
  ///     inside it, and the effect itself with its behaviours.</summary>
  private static P.ParallelTimeNode ClickStep(PptxAnimationChange change, uint next)
    => new(new P.CommonTimeNode(
        new P.StartConditionList(new P.Condition { Delay = Indefinite }),
        new P.ChildTimeNodeList(new P.ParallelTimeNode(new P.CommonTimeNode(
            new P.StartConditionList(new P.Condition { Delay = "0" }),
            new P.ChildTimeNodeList(new P.ParallelTimeNode(EffectNode(change, next + 2))))
        { Id = next + 1, Fill = P.TimeNodeFillValues.Hold })))
    { Id = next, Fill = P.TimeNodeFillValues.Hold });

  /// <summary>The effect node itself: its preset, its click trigger and its behaviours.
  ///     </summary>
  private static P.CommonTimeNode EffectNode(PptxAnimationChange change, uint id)
  {
    string duration = Text(change.DurationMilliseconds ?? DefaultDuration);
    P.CommonTimeNode node = new(
        new P.StartConditionList(new P.Condition { Delay = "0" }),
        new P.ChildTimeNodeList(Visibility(change.TargetShapeId, id + 1)))
    {
      Id = id,
      Duration = duration,
      PresetId = change.Effect?.PresetId ?? DefaultPresetId,
      PresetClass = P.TimeNodePresetClassValues.Entrance,
      PresetSubtype = 0,
      GroupId = 0,
      Fill = P.TimeNodeFillValues.Hold,
      NodeType = P.TimeNodeValues.ClickEffect,
    };
    if (change.Effect?.Filter is { } filter)
    {
      node.ChildTimeNodeList?.Append(Filter(change.TargetShapeId, id + 2, filter, duration));
    }

    return node;
  }

  /// <summary>The behaviour that makes a shape visible, which every entrance effect
  ///     carries.</summary>
  private static P.SetBehavior Visibility(uint shapeId, uint id)
    => new(new P.CommonBehavior(
        new P.CommonTimeNode(new P.StartConditionList(new P.Condition { Delay = "0" }))
        { Id = id, Duration = "1", Fill = P.TimeNodeFillValues.Hold },
        new P.TargetElement(new P.ShapeTarget { ShapeId = Text(shapeId) }),
        new P.AttributeNameList(new P.AttributeName("style.visibility"))),
        new P.ToVariantValue(new P.StringVariantValue { Val = "visible" }));

  /// <summary>The behaviour that animates a shape in with a filter, which fade and wipe
  ///     carry and appear does not.</summary>
  private static P.AnimateEffect Filter(uint shapeId, uint id, string filter, string duration)
    => new(new P.CommonBehavior(
        new P.CommonTimeNode { Id = id, Duration = duration },
        new P.TargetElement(new P.ShapeTarget { ShapeId = Text(shapeId) })))
    { Transition = P.AnimateEffectTransitionValues.In, Filter = filter };

  /// <summary>A timing element holding an empty main sequence, which is the tree an
  ///     appended effect needs on a slide that has no animation at all.</summary>
  private static P.TimeNodeList NewTiming(P.Slide slide)
  {
    P.TimeNodeList list = new(new P.ParallelTimeNode(new P.CommonTimeNode(
        new P.ChildTimeNodeList(new P.SequenceTimeNode(new P.CommonTimeNode
        { Id = 2, Duration = Indefinite, NodeType = P.TimeNodeValues.MainSequence })
        { Concurrent = true, NextAction = P.NextActionValues.Seek }))
    { Id = 1, Duration = Indefinite, NodeType = P.TimeNodeValues.TmingRoot }));
    slide.Timing = new P.Timing(list);
    return list;
  }

  /// <summary>Removes one effect and renumbers the effects after it, which is the order
  ///     they appear in. Removing the last effect removes the timing element, so the
  ///     slide carries no empty animation tree.</summary>
  private static void RemoveEffect(SlidePart slidePart, int index)
  {
    List<P.CommonTimeNode> effects = Effects(slidePart);
    P.CommonTimeNode node = effects[index - 1];
    OpenXmlElement? step = node.Ancestors<P.ParallelTimeNode>().FirstOrDefault();
    node.Remove();
    RemoveEmptySteps(step);
    if (Effects(slidePart).Count == 0)
    {
      slidePart.Slide?.Timing?.Remove();
    }

    slidePart.Slide?.Save();
  }

  /// <summary>Removes the timing steps an effect left empty, from the innermost outwards.
  ///     A step that still holds another effect is left alone.</summary>
  private static void RemoveEmptySteps(OpenXmlElement? step)
  {
    OpenXmlElement? current = step;
    while (current is P.ParallelTimeNode par && !par.Descendants<P.CommonTimeNode>().Any(PptxShapeWalker.IsEffect))
    {
      OpenXmlElement? parent = par.Parent;
      par.Remove();
      current = parent;
    }
  }

  private static uint NextId(P.TimeNodeList list)
    => list.Descendants<P.CommonTimeNode>().Select(node => node.Id?.Value ?? 0U).DefaultIfEmpty(0U).Max() + 1U;

  private static Result<SlidePart> SlideAt(IReadOnlyList<SlidePart> slides, int slide)
    => slide >= 1 && slide <= slides.Count
        ? Result.Success(slides[slide - 1])
        : Result.Failure<SlidePart>(new DomainError(PptxErrorCodes.SlideNotFound,
            $"Slide {slide} does not exist: the deck has {slides.Count} slide(s), numbered 1 to {slides.Count}."));

  private static string Key(int slide, int index) => PptxAddress.ForAnimation(slide, index).Render();

  private static string Missing(int slide, int index, int last)
    => $"Effect {index} does not exist on slide {slide}: the slide has {last} effect(s), so an effect index runs 1 to {last + 1}.";

  /// <summary>A duration as the text a timing node carries, or null when the text is not
  ///     a whole number of milliseconds.</summary>
  private static string? Milliseconds(string? text)
    => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
        ? Text(value)
        : null;

  private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

  private static string Text(uint value) => value.ToString(CultureInfo.InvariantCulture);

  private static Result<PptxResolvedEdit> Failed(string code, string message)
    => Result.Failure<PptxResolvedEdit>(new DomainError(code, message));

  private static Result<T> Failure<T>(string code, string message)
    => Result.Failure<T>(new DomainError(code, message));

  private static Result<T> Unsupported<T>(string message)
    => Result.Failure<T>(new DomainError(PptxErrorCodes.OperationUnsupported, message));

  /// <summary>The preset an added effect takes when the operation names none, which is
  ///     the plainest entrance PowerPoint writes.</summary>
  private const int DefaultPresetId = 1;

  /// <summary>The duration an added effect takes when the operation names none, in
  ///     milliseconds.</summary>
  private const int DefaultDuration = 500;
}
