# Dashboard App

詳細設計:

- ../../docs/web-implementation-design.md

採用stack:

```text
Next.js App Router
TypeScript
Tailwind CSS
shadcn/ui
Radix UI
lucide-react
TanStack Query
React Hook Form
Zod
Supabase JS
openapi-typescript
openapi-fetch
Playwright
Vitest
```

責務:

- session作成。
- paper/poster/notes/avatar/voice asset管理。
- beginner/master/professional、ja/en別Script Agent生成結果のreview/approval。
- TTS cache管理。
- Q&A escalation handling。
- runtime manifest publish。

最初の検証:

```text
1. Supabase login
2. Create session
3. Upload A0 poster
4. Upload paper or text fixture
5. Generate master/ja script
6. Approve segments
7. Generate TTS
8. Publish runtime manifest
9. Playwrightで上記をE2E化
```

Supabase client設定:

```text
NEXT_PUBLIC_SUPABASE_URL=https://<project-ref>.supabase.co
NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_...
NEXT_PUBLIC_API_BASE_URL=https://your-api.example.com
```

新規projectでは `NEXT_PUBLIC_SUPABASE_ANON_KEY` ではなく `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY` を使う。legacy projectだけanon key fallbackを許可する。
