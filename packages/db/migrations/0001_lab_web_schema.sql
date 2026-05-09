create extension if not exists vector;
create extension if not exists pgcrypto;

create table if not exists app_users (
  user_id text primary key,
  role text not null check (role in ('admin', 'member')),
  display_name text,
  created_at timestamptz not null default now()
);

create table if not exists sessions (
  id uuid primary key default gen_random_uuid(),
  owner_id text not null references app_users(user_id),
  title text not null,
  abstract text,
  event_name text,
  status text not null default 'draft' check (status in ('draft', 'review', 'published', 'archived')),
  session_code text not null unique,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists assets (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  kind text not null,
  storage_path text not null,
  file_name text not null,
  mime_type text,
  size_bytes bigint,
  status text not null default 'uploaded',
  created_at timestamptz not null default now()
);

create table if not exists poster_configs (
  session_id uuid primary key references sessions(id) on delete cascade,
  poster_asset_id uuid references assets(id),
  poster_format text not null default 'A0',
  orientation text not null default 'portrait' check (orientation in ('portrait', 'landscape')),
  physical_width_m numeric not null,
  physical_height_m numeric not null,
  tracking_reference_name text,
  qr_fallback_enabled boolean not null default true,
  qr_asset_id uuid references assets(id)
);

create table if not exists poster_panels (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  label text not null,
  order_index int not null check (order_index >= 0),
  x numeric not null check (x >= 0 and x <= 1),
  y numeric not null check (y >= 0 and y <= 1),
  width numeric not null check (width > 0 and width <= 1),
  height numeric not null check (height > 0 and height <= 1),
  constraint uq_poster_panels_session_label unique (session_id, label),
  constraint poster_panels_bounds_chk check (x + width <= 1 and y + height <= 1)
);

create table if not exists knowledge_documents (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  asset_id uuid references assets(id),
  kind text not null,
  title text,
  text text,
  language text not null default 'ja' check (language in ('ja', 'en')),
  status text not null default 'pending',
  trust_level text not null default 'primary' check (trust_level in ('primary', 'author_note', 'reference', 'weak')),
  use_for_script boolean not null default true,
  use_for_live_qa boolean not null default true,
  visibility text not null default 'runtime' check (visibility in ('admin_only', 'presenter', 'runtime')),
  source_url text,
  citation_label text,
  reviewed_at timestamptz,
  created_at timestamptz not null default now()
);

create table if not exists knowledge_chunks (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  document_id uuid not null references knowledge_documents(id) on delete cascade,
  panel_id uuid references poster_panels(id),
  chunk_order int not null,
  text text not null,
  language text check (language in ('ja', 'en')),
  embedding vector(1536),
  section_title text,
  page_number int,
  token_count int not null default 0,
  embedding_model text,
  embedding_dimensions int,
  embedding_status text not null default 'pending',
  embedding_json text,
  retrieval_enabled boolean not null default true,
  script_enabled boolean not null default true,
  qa_enabled boolean not null default true,
  quality_status text not null default 'unreviewed',
  metadata_json text not null default '{}',
  created_at timestamptz not null default now()
);

create table if not exists simulated_qas (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  profile text not null check (profile in ('beginner', 'master', 'professional')),
  language text not null check (language in ('ja', 'en')),
  panel_id uuid references poster_panels(id),
  question text not null,
  answer text not null,
  evidence_chunk_ids_json text not null default '[]',
  status text not null default 'draft' check (status in ('draft', 'approved', 'rejected')),
  source text not null default 'generated',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

alter table knowledge_documents add column if not exists trust_level text not null default 'primary';
alter table knowledge_documents add column if not exists use_for_script boolean not null default true;
alter table knowledge_documents add column if not exists use_for_live_qa boolean not null default true;
alter table knowledge_documents add column if not exists visibility text not null default 'runtime';
alter table knowledge_documents add column if not exists source_url text;
alter table knowledge_documents add column if not exists citation_label text;
alter table knowledge_documents add column if not exists reviewed_at timestamptz;
alter table knowledge_chunks add column if not exists section_title text;
alter table knowledge_chunks add column if not exists page_number int;
alter table knowledge_chunks add column if not exists token_count int not null default 0;
alter table knowledge_chunks add column if not exists embedding_model text;
alter table knowledge_chunks add column if not exists embedding_dimensions int;
alter table knowledge_chunks add column if not exists embedding_status text not null default 'pending';
alter table knowledge_chunks add column if not exists embedding_json text;
alter table knowledge_chunks add column if not exists retrieval_enabled boolean not null default true;
alter table knowledge_chunks add column if not exists script_enabled boolean not null default true;
alter table knowledge_chunks add column if not exists qa_enabled boolean not null default true;
alter table knowledge_chunks add column if not exists quality_status text not null default 'unreviewed';

create table if not exists evidence_claims (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  claim text not null,
  support_status text not null,
  evidence_chunk_ids_json text not null default '[]',
  limitation text,
  created_at timestamptz not null default now()
);

create table if not exists script_segments (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  profile text not null check (profile in ('beginner', 'master', 'professional')),
  language text not null check (language in ('ja', 'en')),
  panel_id uuid references poster_panels(id),
  segment_order int not null,
  segment_type text not null,
  text text not null,
  evidence_chunk_ids_json text not null default '[]',
  status text not null default 'draft',
  tts_asset_id uuid,
  duration_estimate_sec int,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists tts_assets (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  segment_id uuid references script_segments(id) on delete cascade,
  asset_id uuid not null references assets(id),
  provider text not null default 'elevenlabs',
  voice_id text not null,
  model_id text not null,
  language text not null check (language in ('ja', 'en')),
  duration_sec numeric,
  cache_key text not null,
  created_at timestamptz not null default now()
);

create table if not exists voice_consents (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  voice_id text not null,
  provider text not null default 'elevenlabs',
  confirmed_by_user_id text not null references app_users(user_id),
  consent_label text not null,
  notes text,
  created_at timestamptz not null default now()
);

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'script_segments_tts_asset_id_fkey'
  ) then
    alter table script_segments
      add constraint script_segments_tts_asset_id_fkey
      foreign key (tts_asset_id) references tts_assets(id);
  end if;
end $$;

create table if not exists presentation_cues (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  segment_id uuid references script_segments(id) on delete cascade,
  cue_type text not null check (
    cue_type in (
      'panel_focus',
      'pointer_move',
      'caption_show',
      'caption_hide',
      'audio_play',
      'qr_show',
      'ui_panel_show'
    )
  ),
  start_ms int not null check (start_ms >= 0),
  duration_ms int check (duration_ms is null or duration_ms >= 0),
  payload jsonb not null default '{}',
  created_at timestamptz not null default now()
);

create table if not exists jobs (
  id uuid primary key default gen_random_uuid(),
  session_id uuid references sessions(id) on delete cascade,
  kind text not null,
  status text not null default 'queued',
  input jsonb not null default '{}',
  output jsonb not null default '{}',
  error text,
  attempt_count int not null default 0,
  locked_at timestamptz,
  locked_by text,
  run_after timestamptz not null default now(),
  finished_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists questions (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references sessions(id) on delete cascade,
  transcript text not null,
  profile text not null check (profile in ('beginner', 'master', 'professional')),
  language text not null check (language in ('ja', 'en')),
  active_panel_id uuid references poster_panels(id),
  answerability text,
  answer text,
  evidence_chunk_ids uuid[] not null default '{}',
  escalation_reason text,
  created_at timestamptz not null default now()
);

create index if not exists sessions_owner_id_idx on sessions(owner_id);
create index if not exists app_users_role_idx on app_users(role);
create index if not exists assets_session_id_idx on assets(session_id);
create index if not exists poster_panels_session_order_idx on poster_panels(session_id, order_index);
create index if not exists knowledge_documents_session_id_idx on knowledge_documents(session_id);
create index if not exists knowledge_chunks_session_id_idx on knowledge_chunks(session_id);
create index if not exists simulated_qas_session_profile_language_idx
  on simulated_qas(session_id, profile, language);
create index if not exists script_segments_session_profile_language_idx
  on script_segments(session_id, profile, language);
create index if not exists presentation_cues_session_segment_idx
  on presentation_cues(session_id, segment_id);
create index if not exists jobs_session_status_idx on jobs(session_id, status);
create index if not exists questions_session_id_idx on questions(session_id);
create index if not exists voice_consents_session_voice_idx on voice_consents(session_id, voice_id);

create index if not exists knowledge_chunks_embedding_hnsw_idx
  on knowledge_chunks using hnsw (embedding vector_cosine_ops)
  where embedding is not null;

alter table app_users enable row level security;
alter table sessions enable row level security;
alter table assets enable row level security;
alter table poster_configs enable row level security;
alter table poster_panels enable row level security;
alter table knowledge_documents enable row level security;
alter table knowledge_chunks enable row level security;
alter table evidence_claims enable row level security;
alter table simulated_qas enable row level security;
alter table script_segments enable row level security;
alter table tts_assets enable row level security;
alter table voice_consents enable row level security;
alter table presentation_cues enable row level security;
alter table jobs enable row level security;
alter table questions enable row level security;

do $$
begin
  if not exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'sessions' and policyname = 'sessions_service_role_all'
  ) then
    execute 'create policy sessions_service_role_all on sessions for all using (auth.role() = ''service_role'') with check (auth.role() = ''service_role'')';
  end if;
  if not exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'sessions' and policyname = 'sessions_member_access'
  ) then
    execute $policy$
      create policy sessions_member_access on sessions
      for all
      using (
        auth.role() = 'authenticated'
        and (
          (auth.jwt() ->> 'app_role') = 'admin'
          or owner_id = (auth.jwt() ->> 'sub')
        )
      )
      with check (
        auth.role() = 'authenticated'
        and (
          (auth.jwt() ->> 'app_role') = 'admin'
          or owner_id = (auth.jwt() ->> 'sub')
        )
      )
    $policy$;
  end if;
end $$;

do $$
declare
  table_name text;
begin
  foreach table_name in array array[
    'assets',
    'poster_configs',
    'poster_panels',
    'knowledge_documents',
    'knowledge_chunks',
    'evidence_claims',
    'simulated_qas',
    'script_segments',
    'tts_assets',
    'voice_consents',
    'presentation_cues',
    'jobs',
    'questions'
  ]
  loop
    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and policyname = table_name || '_service_role_all'
    ) then
      execute format(
        'create policy %I on %I for all using (auth.role() = ''service_role'') with check (auth.role() = ''service_role'')',
        table_name || '_service_role_all',
        table_name
      );
    end if;
    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and policyname = table_name || '_member_access'
    ) then
      execute format(
        $sql$
          create policy %I on %I
          for all
          using (
            auth.role() = 'authenticated'
            and exists (
              select 1
              from sessions
              where sessions.id = %I.session_id
                and (
                  (auth.jwt() ->> 'app_role') = 'admin'
                  or sessions.owner_id = (auth.jwt() ->> 'sub')
                )
            )
          )
          with check (
            auth.role() = 'authenticated'
            and exists (
              select 1
              from sessions
              where sessions.id = %I.session_id
                and (
                  (auth.jwt() ->> 'app_role') = 'admin'
                  or sessions.owner_id = (auth.jwt() ->> 'sub')
                )
            )
          )
        $sql$,
        table_name || '_member_access',
        table_name,
        table_name,
        table_name
      );
    end if;
  end loop;
end $$;
