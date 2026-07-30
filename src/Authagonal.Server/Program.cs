/// <summary>
/// Logger-category anchor for the endpoint handlers, which take <c>ILogger&lt;Program&gt;</c>.
/// <para>
/// Until the deployable was split out into <c>Authagonal.Host</c>, this project carried the
/// entrypoint and those loggers resolved against its generated top-level <c>Program</c>, giving the
/// category name <c>Program</c>. Keeping an anchor of the same name in the same (global) namespace
/// keeps every category byte-identical, so existing log filters and queries still match. It is
/// <c>internal</c>, exactly as the generated class was, so nothing leaks into consumers.
/// </para>
/// <para>
/// Worth revisiting: library code logging under a category called "Program" describes the host rather
/// than the component. Renaming it is a visible change to anyone filtering on it, so it belongs in its
/// own change, not this one.
/// </para>
/// </summary>
internal sealed class Program;
