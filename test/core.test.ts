import { describe, expect, it } from 'vitest'
import {
  FINISH_COOLDOWN_MS,
  describeApprovalReason,
  isRootAgent,
  isSupportedPlatform,
  resolveApprovalText,
  resolveQuestionText,
  resolveSessionTitle,
  truncate,
  type SessionTitleService,
} from '../src/core'

describe('truncate', () => {
  it('returns empty string unchanged', () => {
    expect(truncate('', 10)).toBe('')
  })

  it('keeps short strings intact', () => {
    expect(truncate('abc', 10)).toBe('abc')
  })

  it('clips long strings with a single ellipsis', () => {
    expect(truncate('abcdefghij', 5)).toBe('abcde…')
    expect(truncate('abcdefghij', 5).length).toBe(6)
  })

  it('handles CJK strings by code units', () => {
    expect(truncate('任务完成通知测试', 4)).toBe('任务完成…')
  })
})

describe('isSupportedPlatform', () => {
  it('accepts only win32', () => {
    expect(isSupportedPlatform('win32')).toBe(true)
    expect(isSupportedPlatform('linux')).toBe(false)
    expect(isSupportedPlatform('darwin')).toBe(false)
    expect(isSupportedPlatform('freebsd')).toBe(false)
  })
})

describe('isRootAgent', () => {
  it('is permissive for unknown shapes', () => {
    expect(isRootAgent(undefined)).toBe(true)
    expect(isRootAgent(null)).toBe(true)
    expect(isRootAgent({})).toBe(true)
  })

  it('treats a plain session without header as root', () => {
    expect(isRootAgent({ session: {} })).toBe(true)
  })

  it('skips subagent-origin sessions', () => {
    expect(isRootAgent({ session: { header: { origin: 'subagent' } } })).toBe(false)
  })

  it('skips delegated sessions (delegationDepth > 0)', () => {
    expect(isRootAgent({ session: { header: { delegationDepth: 1 } } })).toBe(false)
    expect(isRootAgent({ session: { header: { delegationDepth: 3 } } })).toBe(false)
  })

  it('keeps forked sessions (parentSession without origin) user-facing', () => {
    expect(isRootAgent({ session: { header: { parentSession: 'abc' } } })).toBe(true)
  })

  it('tolerates malformed session shapes', () => {
    expect(isRootAgent('not-an-object')).toBe(true)
    expect(isRootAgent(42)).toBe(true)
  })
})

describe('resolveSessionTitle', () => {
  const service: SessionTitleService = new Map([['s1', { title: '会话标题' }]]) as unknown as SessionTitleService

  it('returns the title when present', () => {
    expect(resolveSessionTitle({ session: 's1' }, service)).toBe('会话标题')
  })

  it('returns undefined when the session is unknown', () => {
    expect(resolveSessionTitle({ session: 's2' }, service)).toBeUndefined()
  })

  it('returns undefined without a service or session', () => {
    expect(resolveSessionTitle({ session: 's1' }, undefined)).toBeUndefined()
    expect(resolveSessionTitle({}, service)).toBeUndefined()
    expect(resolveSessionTitle(null, service)).toBeUndefined()
  })

  it('returns undefined for empty titles', () => {
    const empty: SessionTitleService = new Map([['s1', { title: '' }]]) as unknown as SessionTitleService
    expect(resolveSessionTitle({ session: 's1' }, empty)).toBeUndefined()
  })
})

describe('resolveQuestionText', () => {
  it('extracts the first question with a count suffix', () => {
    const text = resolveQuestionText({
      name: 'ask_user_question',
      arguments: { questions: [{ question: 'A' }, { header: 'B' }] },
    })
    expect(text).toBe('问题：A（共 2 个问题）')
  })

  it('reads args/input aliases and header fallback', () => {
    expect(resolveQuestionText({ args: { questions: [{ header: 'H' }] } })).toBe('问题：H')
    expect(resolveQuestionText({ input: { questions: [{ question: 'Q' }] } })).toBe('问题：Q')
  })

  it('truncates long questions', () => {
    const long = 'x'.repeat(100)
    const text = resolveQuestionText({ arguments: { questions: [{ question: long }] } })
    expect(text).toBe('问题：' + 'x'.repeat(80) + '…')
  })

  it('returns undefined for empty/missing question lists', () => {
    expect(resolveQuestionText({ arguments: {} })).toBeUndefined()
    expect(resolveQuestionText({ arguments: { questions: [] } })).toBeUndefined()
    expect(resolveQuestionText({ arguments: { questions: [{ foo: 1 }] } })).toBeUndefined()
    expect(resolveQuestionText(null)).toBeUndefined()
    expect(resolveQuestionText('nope')).toBeUndefined()
  })
})

describe('resolveApprovalText', () => {
  it('combines tool name and reason', () => {
    expect(resolveApprovalText({ toolName: 'write', reason: '创建文件' })).toBe('操作：write；原因：创建文件')
  })

  it('supports tool object forms and description fallback', () => {
    expect(resolveApprovalText({ tool: { name: 'edit' }, description: '修改配置' })).toBe('操作：edit；原因：修改配置')
    expect(resolveApprovalText({ tool: 'pwsh', reason: '执行命令' })).toBe('操作：pwsh；原因：执行命令')
  })

  it('tolerates partial information', () => {
    expect(resolveApprovalText({ toolName: 'write' })).toBe('操作：write')
    expect(resolveApprovalText({ reason: 'x' })).toBe('需要人工审批；模型原文：x')
  })

  it('returns undefined when nothing usable is present', () => {
    expect(resolveApprovalText({})).toBeUndefined()
    expect(resolveApprovalText(null)).toBeUndefined()
    expect(resolveApprovalText(undefined)).toBeUndefined()
  })

  it('localizes the host escalation template into Chinese', () => {
    const text = resolveApprovalText({
      toolName: 'pwsh',
      reason: 'escalate sandbox to danger-full-access: Same sandbox denial as before.',
    })
    expect(text).toBe('操作：pwsh；沙箱提权到 danger-full-access（可写工作区外任意路径，不可自动回滚）；模型原文：Same sandbox denial as before.')
    expect(text).not.toContain('escalate sandbox to')
  })

  it('keeps an already-Chinese escalation reason and only drops the English prefix', () => {
    expect(resolveApprovalText({ toolName: 'write', reason: 'escalate sandbox to workspace-write: 写入工作区报告文件。' }))
      .toBe('操作：write；沙箱提权到 workspace-write：写入工作区报告文件。')
  })
})

describe('describeApprovalReason', () => {
  it('renders every known sandbox mode with its consequence', () => {
    expect(describeApprovalReason('escalate sandbox to read-only: x')).toBe('沙箱提权到 read-only（只读，不会改文件）；模型原文：x')
    expect(describeApprovalReason('escalate sandbox to workspace-write: x')).toBe('沙箱提权到 workspace-write（可改工作区内文件）；模型原文：x')
    expect(describeApprovalReason('escalate sandbox to danger-full-access: x')).toBe('沙箱提权到 danger-full-access（可写工作区外任意路径，不可自动回滚）；模型原文：x')
  })

  it('never claims a consequence for an unknown mode', () => {
    expect(describeApprovalReason('escalate sandbox to weird-mode: x')).toBe('沙箱提权到 weird-mode（未识别的沙箱模式）；模型原文：x')
  })

  it('passes Chinese reasons through and truncates them', () => {
    expect(describeApprovalReason('编辑 Merge.yaml')).toBe('原因：编辑 Merge.yaml')
    expect(describeApprovalReason('中文'.repeat(70))).toBe('原因：' + '中文'.repeat(60) + '…')
  })

  it('appends the English original instead of dropping it', () => {
    expect(describeApprovalReason('Needs a human check.')).toBe('需要人工审批；模型原文：Needs a human check.')
  })

  it('accepts an approval-gate already-localized reason without double-wrapping', () => {
    const localized = '沙箱提权到 danger-full-access：可读写工作区之外的任意路径（含系统位置），改动不再受沙箱限制，也无法自动回滚。\n模型说明原文（未翻译）：Same denial.'
    const out = describeApprovalReason(localized)
    // No redundant 「原因：」 label in front of the plugin's own Chinese lead…
    expect(out && out.startsWith('沙箱提权到 danger-full-access：')).toBe(true)
    expect(out && out.startsWith('原因：')).toBe(false)
    // …and the consequence sentence survives the toast budget intact.
    expect(out && out.includes('可读写工作区之外的任意路径（含系统位置），改动不再受沙箱限制，也无法自动回滚。')).toBe(true)
  })

  it('localizes the prefix of an approval-gate reason that carries a Chinese detail', () => {
    expect(describeApprovalReason('escalate sandbox to danger-full-access: 这是中文说明。'))
      .toBe('沙箱提权到 danger-full-access：这是中文说明。')
  })

  it('returns undefined for empty or non-string reasons', () => {
    expect(describeApprovalReason(undefined)).toBeUndefined()
    expect(describeApprovalReason('')).toBeUndefined()
    expect(describeApprovalReason('   ')).toBeUndefined()
    expect(describeApprovalReason(42)).toBeUndefined()
  })
})

describe('constants', () => {
  it('exposes the completion cooldown', () => {
    expect(FINISH_COOLDOWN_MS).toBe(10_000)
  })
})