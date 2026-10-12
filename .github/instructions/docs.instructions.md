---
applyTo: '**/*.cs'
description: 'XML documentation and approachable sample-code comment guidance for maddyBot.'
---

# C# Documentation Best Practices

Adapted from `blowdart/idunno.Bluesky`, branch `version/v8.0.0`.

- Document public members with XML comments.
- Document internal classes and their constructors with XML comments too.
  Internal visibility is not a reason to omit documentation. Use implementation
  comments additionally where they explain non-obvious decisions.
- Document private functions and constructors with XML comments as well. Explain
  their purpose, inputs, results, and important recovery decisions in language a
  beginner can follow; accessibility does not reduce the need for documentation.
- Explain complex internal behavior, especially persistence, replay, cancellation
  and uncertain writes. Plain implementation comments are appropriate for these.
- This is a sample application: explain how components fit together and why an
  approach was chosen in terms a reader new to the SDK can understand.
- Keep the README and customization comments consistent. Do not promise
  exactly-once delivery, unlimited recovery or other guarantees the code cannot provide.

## Guidance for all APIs

- Use `<summary>` for a brief, one-sentence description. Start with a present-tense,
  third-person verb.
- Use `<remarks>` for implementation details or usage notes, with one `<para>`
  element per paragraph.
- Use `<see langword>` for language keywords such as `null`, `true` and `false`.
- Use `<c>` for inline code and `<paramref>` / `<typeparamref>` for parameter references.
- Use `<see cref>` for inline type/member references and `<seealso>` for standalone references.
- Put usage examples in `<example>` with `<code language="csharp">`.
- Use `<inheritdoc/>` for inherited behavior unless important differences need explanation.

## Tag ordering

Order tags as follows, omitting those that do not apply:

1. `<summary>`
2. `<typeparam>`
3. `<param>`
4. `<returns>` or `<value>`
5. `<exception>`
6. `<remarks>`
7. `<example>`
8. `<seealso>`

Parameter and exception tags precede remarks, including on primary-constructor types.

## Methods

- Describe each parameter using `<param>`, with a noun phrase starting with an
  introductory article, without repeating its data type.
- For flag enums, use "A bitwise combination of the enumeration values that specifies...".
- For non-flag enums, use "One of the enumeration values that specifies...".
- For Boolean parameters, use "`<see langword="true"/>` to ...; otherwise,
  `<see langword="false"/>`.".
- For output parameters, use "When this method returns, contains ... .
  This parameter is treated as uninitialized.".
- Document type parameters with `<typeparam>`.
- Describe returns with a noun phrase and introductory article. For Boolean
  returns, use "`<see langword="true"/>` if ...; otherwise, `<see langword="false"/>`.".

## Constructors

Use "Initializes a new instance of the ... class [or struct]." for the summary.

## Properties

- Start summaries with "Gets or sets..." or "Gets..." as appropriate.
- For Boolean properties, use "Gets [or sets] a value that indicates whether...".
- Describe the value in `<value>` without repeating the data type.
- State defaults separately, for example "The default is `<see langword="false"/>`.".
- Explain both Boolean outcomes where useful.

## Exceptions

- Document directly thrown exceptions with `<exception cref>`.
- For nested calls, document the exceptions callers are most likely to encounter.
- Describe the triggering condition directly, without starting with "Thrown if..." or "If...".
- Distinguish retryable dependency failures, intentional cancellation and invariant
  failures accurately; documentation must not imply that every exception is swallowed.
