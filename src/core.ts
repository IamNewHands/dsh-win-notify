/**
 * dsh-win-notify — pure, testable core.
 *
 * Everything here is free of `ctx`, `node:*` imports and Windows APIs so the
 * vitest suite can regression-test it on any platform (CI included). The
 * plugin entry (`src/index.ts`) wires these into DSH host events.
 */

/** Minimum gap between two "任务完成" toasts of the same agent (ms). */
export const FINISH_COOLDOWN_MS = 10_000

/**
 * How long an approval request must stay unanswered before it is worth a toast
 * (ms).
 *
 * DSH 2.0.21+ composes one *terminal* answerer for `approval/request`: the
 * desktop approval panel (`@deepseek-ai/dsh-client-ui-approval`) is reached
 * through the api-remotes waterfall and RETURNS an outcome, which ends the
 * chain — so any listener registered after it (this plugin, bundle index last)
 * never sees the request. The fix is to register with `{ prepend: true }` and
 * answer nothing, which puts this plugin in front of the panel. That alone
 * would toast every request, including the ones an auto-approver answers in
 * milliseconds. So the toast is deferred by this window: a gate or policy that
 * resolves the request inside it produces no toast, while a request still
 * pending after it is one a human actually has to answer.
 */
export const PENDING_GRACE_MS = 1_000

/**
 * True only on Windows. The WinRT Toast helper (`.dsh-notify/DshToast.exe`)
 * is a Windows-only binary; declaring `"os": ["win32"]` in package.json is the
 * install-time gate and this guard is the runtime gate (apply() no-ops).
 */
export function isSupportedPlatform(platform: NodeJS.Platform): boolean {
  return platform === 'win32'
}

/** Clip a string to `max` chars, adding a single ellipsis when truncated. */
export function truncate(text: string, max: number): string {
  if (text.length === 0) return text
  return text.length > max ? text.slice(0, max) + '…' : text
}

/** The minimal session-header shape isRootAgent inspects. */
export interface AgentLike {
  session?: {
    header?: {
      /** Set to 'subagent' by the subagent machinery for in-process children. */
      origin?: string
      /** Present (> 0) on delegated/subagent runs. */
      delegationDepth?: number
    }
  }
}

/**
 * True only for user-facing root agents. Subagents — plain `subagent` runs,
 * AgentTeams members and workflow workers all being in-process children — are
 * marked `origin: "subagent"` (+ `delegationDepth`) in their session header by
 * the subagent machinery. Their running→idle churn must never raise toasts.
 * Forked sessions (`parentSession` without `origin`) stay user-facing.
 */
export function isRootAgent(agent: unknown): boolean {
  try {
    const header = agent && typeof agent === 'object'
      ? (agent as AgentLike).session?.header
      : undefined
    if (!header || typeof header !== 'object') return true // unknown shape: be permissive
    if (header.origin === 'subagent') return false
    if (typeof header.delegationDepth === 'number' && header.delegationDepth > 0) return false
    return true
  } catch {
    return true
  }
}

/** The session-title service face this plugin relies on (duck-typed). */
export interface SessionTitleService {
  get(session: unknown): { title?: string } | undefined
}

/** Subprocess service surfaces used by the plugin (duck-typed, structural). */
export interface SubprocessHandle {
  done?: Promise<unknown>
}

export interface SubprocessService {
  spawn(opts: { argv: string[]; cwd?: string; stdio?: unknown; graceMs?: number }): SubprocessHandle
}

/**
 * The minimal DSH host context this plugin consumes. Structural typing keeps
 * the plugin (and its published types) free of runtime dependencies: the real
 * DSH `Context` satisfies this shape, and tests can hand in a plain mock.
 */
export interface NotifyContext {
  get<T = unknown>(name: string): T
  /**
   * Cordis listener registration. `{ prepend: true }` unshifts the listener to
   * the FRONT of the waterfall — required for `approval/request`, see
   * {@link PENDING_GRACE_MS}.
   */
  on(event: string, listener: (...args: any[]) => unknown, options?: { prepend?: boolean }): unknown
  subprocess: SubprocessService
}

/**
 * Human title of the session this agent is running, when one exists.
 * Resolved directly from `agent.session` (a WeakMap-keyed service lookup that
 * often missed on web sessions) — falls back to undefined on any failure.
 */
export function resolveSessionTitle(agent: unknown, sessionTitle: SessionTitleService | undefined): string | undefined {
  try {
    if (!sessionTitle || typeof agent !== 'object' || agent === null) return undefined
    const session = (agent as AgentLike).session
    if (!session) return undefined
    const snapshot = sessionTitle.get(session)
    if (snapshot && typeof snapshot.title === 'string' && snapshot.title.length > 0) return snapshot.title
  } catch {
    // Any extraction failure falls back to a generic message.
  }
  return undefined
}

/**
 * One-line label for an ask_user_question tool execution. Reads the first
 * question's text (or header) and appends a count when several questions are
 * asked at once.
 */
export function resolveQuestionText(exec: unknown): string | undefined {
  try {
    const e = exec as { arguments?: { questions?: unknown[] }, args?: { questions?: unknown[] }, input?: { questions?: unknown[] } }
    const args = e.arguments || e.args || e.input
    const questions = args && args.questions
    if (!Array.isArray(questions) || questions.length === 0) return undefined
    const first = questions[0] as { question?: string, header?: string } | undefined
    const text = first && (first.question || first.header)
    if (typeof text !== 'string' || text.length === 0) return undefined
    const label = '问题：' + truncate(text, 80)
    return questions.length > 1 ? label + '（共 ' + questions.length + ' 个问题）' : label
  } catch {
    return undefined
  }
}

/**
 * True when the text already contains CJK characters (i.e. it is already a
 * Chinese explanation). Shared with dsh-approval-gate's `src/zh.mjs` semantics
 * so the toast never double-wraps a reason the host already localized.
 */
export function hasCJK(text: unknown): boolean {
  return /[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/.test(text == null ? '' : String(text))
}

/** Short consequence phrase per sandbox mode, sized for a toast body. */
const MODE_EFFECT: Record<string, string> = {
  'read-only': '只读，不会改文件',
  'workspace-write': '可改工作区内文件',
  'danger-full-access': '可写工作区外任意路径，不可自动回滚',
}

/** The host's escalation template: `escalate sandbox to <mode>: <justification>`. */
const ESCALATE_RE = /escalate\s+sandbox\s+to\s+([^\s:]+):?\s*([\s\S]*)/i

/** Chinese lead another host plugin (dsh-approval-gate) puts in front of its rewrite. */
const ZH_ESCALATION_LEAD = /^沙箱提权到/u

/** Toast budget for the Chinese explanation: enough to keep the consequence sentence. */
const REASON_MAX = 120

/** Toast budget for the appended original text. */
const NOTE_MAX = 80

/**
 * Chinese, approver-facing form of an approval request's reason.
 *
 * The model's `justification` is often English (subagents and other providers
 * especially) and the host template adds an English prefix, so a toast built
 * from the raw reason is unreadable for a Chinese approver. This rebuilds it
 * from the one structured fact in the payload — the target sandbox mode — and
 * appends the model's original text as a note. Reasons that already contain
 * Chinese are never rewritten: an English template prefix is localized, and a
 * reason another plugin already led with 「沙箱提权到 …」 keeps its own wording
 * instead of getting a redundant 「原因：」 label prepended.
 *
 * @param reason - the raw approval `reason` (or `description`).
 * @returns the Chinese explanation, or undefined when the reason is empty.
 */
export function describeApprovalReason(reason: unknown): string | undefined {
  const raw = typeof reason === 'string' ? reason.trim() : ''
  if (raw.length === 0) return undefined
  const matched = ESCALATE_RE.exec(raw)
  if (matched) {
    const mode = matched[1]
    const detail = (matched[2] || '').trim()
    // Already-Chinese detail (e.g. dsh-approval-gate rewrote the reason): only
    // localize the host prefix, never rewrite the wording.
    if (hasCJK(detail)) return '沙箱提权到 ' + mode + '：' + truncate(detail, REASON_MAX)
    const effect = MODE_EFFECT[mode] || '未识别的沙箱模式'
    const note = detail.length > 0 ? '；模型原文：' + truncate(detail, NOTE_MAX) : ''
    return '沙箱提权到 ' + mode + '（' + effect + '）' + note
  }
  if (hasCJK(raw)) {
    return ZH_ESCALATION_LEAD.test(raw) ? truncate(raw, REASON_MAX) : '原因：' + truncate(raw, REASON_MAX)
  }
  return '需要人工审批；模型原文：' + truncate(raw, NOTE_MAX)
}

/**
 * One-line label for a pending approval request: tool name + reason. Either
 * may be absent; returns undefined only when nothing usable is present.
 * The reason half is always Chinese-led (see {@link describeApprovalReason}).
 */
export function resolveApprovalText(req: unknown): string | undefined {
  try {
    const r = req as {
      reason?: string, description?: string, toolName?: string,
      tool?: { name?: string } | string,
    }
    const reason = r.reason || r.description
    const toolObj = r.tool
    const toolName = r.toolName || (toolObj && typeof toolObj === 'object' && toolObj.name) || (typeof toolObj === 'string' ? toolObj : undefined)
    const parts: string[] = []
    if (typeof toolName === 'string' && toolName.length > 0) parts.push('操作：' + truncate(toolName, 40))
    const described = describeApprovalReason(reason)
    if (described !== undefined) parts.push(described)
    return parts.length > 0 ? parts.join('；') : undefined
  } catch {
    return undefined
  }
}

/** How far back the approval scan looks for the `approval/asked` event. */
const APPROVAL_SCAN_LIMIT = 200

/**
 * The slice of a DSH session log the approval helpers read. Both accessors are
 * duck-typed because the session surface differs between DSH releases: newer
 * builds expose `eventAt(seq)` plus `seq`, older ones only `snapshotEvents()`.
 * `eventAt`/`snapshotEvents` are marked deprecated upstream, hence the tolerant
 * shape and the `undefined` returns instead of throws.
 */
export interface SessionLogLike {
  readonly seq?: number
  eventAt?(seq: number): { type?: string, data?: unknown } | undefined
  snapshotEvents?(fromSeq?: number, toSeqExclusive?: number): readonly { type?: string, data?: unknown }[]
}

/** Where a pending approval sits in the session log. */
export interface ApprovalAnchor {
  /** The `approval/asked` id, when the host recorded one. */
  id?: string
  /** Sequence number of that `approval/asked` event. */
  seq: number
}

/** Log length of a session, or -1 when the surface does not expose it. */
function sessionLength(session: SessionLogLike): number {
  try {
    const seq = session.seq
    return typeof seq === 'number' && seq >= 0 ? seq : -1
  } catch {
    return -1
  }
}

/** Events in `[fromSeq, toSeqExclusive)`; empty when the surface is missing. */
function readEvents(
  session: SessionLogLike,
  fromSeq: number,
  toSeqExclusive: number,
): readonly { type?: string, data?: unknown }[] {
  const length = sessionLength(session)
  const end = toSeqExclusive === Infinity ? length : toSeqExclusive
  if (end < 0) return []
  try {
    if (typeof session.eventAt === 'function') {
      const out: { type?: string, data?: unknown }[] = []
      for (let seq = fromSeq; seq < end; seq++) {
        const event = session.eventAt(seq)
        if (event !== undefined) out.push(event)
      }
      return out
    }
    if (typeof session.snapshotEvents === 'function') {
      const events = session.snapshotEvents(fromSeq, end)
      return Array.isArray(events) ? events : []
    }
  } catch {
    // Deprecated accessors may throw on a closed or compacted log.
  }
  return []
}

/**
 * Find the `approval/asked` event backing a pending `approval/request`, so a
 * later `approval/decided` can be correlated with it.
 *
 * Matching prefers the exact `callId` (the join key dsh-im and the session
 * projections both use) and falls back to the newest `approval/asked` of the
 * same tool name, because the request payload does not always carry a callId.
 *
 * @returns the anchor, or undefined when the session log has no evidence.
 */
export function locateApprovalAsked(
  session: unknown,
  toolName?: string,
  callId?: string,
): ApprovalAnchor | undefined {
  if (!session || typeof session !== 'object') return undefined
  const log = session as SessionLogLike
  const length = sessionLength(log)
  if (length < 0) return undefined
  const from = Math.max(0, length - APPROVAL_SCAN_LIMIT)
  let byTool: ApprovalAnchor | undefined
  for (let seq = length - 1; seq >= from; seq--) {
    let event: { type?: string, data?: unknown } | undefined
    try {
      event = typeof log.eventAt === 'function'
        ? log.eventAt(seq)
        : readEvents(log, seq, seq + 1)[0]
    } catch {
      return undefined
    }
    if (!event || event.type !== 'approval/asked') continue
    const data = event.data as { id?: string, callId?: string, toolName?: string } | undefined
    const id = typeof data?.id === 'string' ? data.id : undefined
    const anchor: ApprovalAnchor = { id, seq }
    if (callId !== undefined && typeof data?.callId === 'string' && data.callId === callId) return anchor
    if (byTool === undefined && toolName !== undefined && typeof data?.toolName === 'string' && data.toolName === toolName) {
      byTool = anchor
    }
  }
  return byTool
}

/**
 * True when the host already answered the approval this anchor points at.
 *
 * `approval/decided` is appended by the approval service AFTER the
 * `approval/request` waterfall settles, so an event with the same id (or any
 * decided event when the id is unknown) means the request is no longer pending
 * and the deferred toast must be dropped.
 */
export function isApprovalDecidedAfter(session: unknown, anchor: ApprovalAnchor): boolean {
  if (!session || typeof session !== 'object') return false
  const log = session as SessionLogLike
  const events = readEvents(log, anchor.seq + 1, Infinity)
  for (const event of events) {
    if (!event || event.type !== 'approval/decided') continue
    const data = event.data as { id?: string } | undefined
    if (anchor.id === undefined || (typeof data?.id === 'string' && data.id === anchor.id)) return true
  }
  return false
}