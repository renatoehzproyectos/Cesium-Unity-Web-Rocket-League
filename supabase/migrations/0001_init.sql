-- OBRA360: esquema, seguridad por fila (RLS) y operaciones atómicas
create extension if not exists pgcrypto with schema extensions;
create table profiles(id uuid primary key references auth.users on delete cascade,email text not null,name text);
create table calendars(id uuid primary key default gen_random_uuid(),name text not null check(length(name) between 1 and 100),type text not null check(type in('personal','shared')),owner_id uuid not null references profiles on delete cascade,created_at timestamptz default now());
create table calendar_members(calendar_id uuid references calendars on delete cascade,user_id uuid references profiles on delete cascade,role text not null check(role in('admin','editor','lector')),primary key(calendar_id,user_id));
create table events(id uuid primary key default gen_random_uuid(),calendar_id uuid not null references calendars on delete cascade,creator_id uuid not null references profiles,title text not null check(length(title) between 1 and 200),type text default 'Otro',status text default 'pendiente' check(status in('pendiente','confirmado','en curso','completado','cancelado')),priority text default 'normal' check(priority in('normal','alta','urgente')),start_at timestamptz not null,end_at timestamptz not null,location text,description text,created_at timestamptz default now(),modified_at timestamptz default now(),check(end_at>=start_at));
create table calendar_invitations(id uuid primary key default gen_random_uuid(),calendar_id uuid not null references calendars on delete cascade,calendar_name text,email text not null,role text not null check(role in('admin','editor','lector')),invited_by uuid references profiles,invited_by_email text,token_hash text unique not null,status text not null default 'pendiente' check(status in('pendiente','aceptada','revocada','vencida')),expires_at timestamptz not null,created_at timestamptz default now());
create table event_history(id bigserial primary key,event_id uuid,calendar_id uuid,user_id uuid,action text,at timestamptz default now());
create index on events(calendar_id,start_at);create index on calendar_invitations(lower(email));

create function has_role(c uuid,r text[]) returns boolean language sql stable security definer set search_path=public as
$$select exists(select 1 from calendar_members where calendar_id=c and user_id=auth.uid() and role=any(r))$$;

create function handle_new_user() returns trigger language plpgsql security definer set search_path=public as $$
begin insert into profiles values(new.id,lower(new.email),coalesce(new.raw_user_meta_data->>'name',new.email));
insert into calendars(name,type,owner_id) values('Personal','personal',new.id);return new;end$$;
create trigger on_auth_user after insert on auth.users for each row execute function handle_new_user();

create function owner_member() returns trigger language plpgsql security definer set search_path=public as $$
begin insert into calendar_members values(new.id,new.owner_id,'admin');return new;end$$;
create trigger on_calendar after insert on calendars for each row execute function owner_member();

create function log_event() returns trigger language plpgsql security definer set search_path=public as $$
begin insert into event_history(event_id,calendar_id,user_id,action) values(coalesce(new.id,old.id),coalesce(new.calendar_id,old.calendar_id),auth.uid(),tg_op);return coalesce(new,old);end$$;
create trigger ev_log after insert or update or delete on events for each row execute function log_event();

-- Mover / copiar: una sola transacción, todo o nada
create function share_event(p_event uuid,p_dest uuid,p_mode text) returns uuid language plpgsql security definer set search_path=public as $$
declare e events;src calendars;dst calendars;n uuid;
begin
 select * into e from events where id=p_event for update;
 if not found then raise exception 'Evento no encontrado';end if;
 select * into src from calendars where id=e.calendar_id;select * into dst from calendars where id=p_dest;
 if src.type<>'personal' or src.owner_id<>auth.uid() then raise exception 'Solo puedes mover o copiar eventos de tu calendario personal';end if;
 if dst.id is null or dst.type<>'shared' or not has_role(p_dest,array['admin','editor']) then raise exception 'Sin permiso en el calendario de destino';end if;
 if p_mode='move' then update events set calendar_id=p_dest,modified_at=now() where id=p_event;return p_event;
 elsif p_mode='copy' then
  insert into events(calendar_id,creator_id,title,type,status,priority,start_at,end_at,location,description)
  values(p_dest,auth.uid(),e.title,e.type,e.status,e.priority,e.start_at,e.end_at,e.location,e.description) returning id into n;return n;
 else raise exception 'Modo no válido';end if;
end$$;

create function accept_invitation(p_id uuid default null,p_token text default null) returns uuid language plpgsql security definer set search_path=public,extensions as $$
declare i calendar_invitations;
begin
 select * into i from calendar_invitations where status='pendiente' and ((p_id is not null and id=p_id) or (p_token is not null and token_hash=encode(digest(p_token,'sha256'),'hex'))) for update;
 if not found or i.expires_at<now() then raise exception 'Invitación no válida o vencida';end if;
 if lower(i.email)<>lower(auth.jwt()->>'email') then raise exception 'Esta invitación es para otro correo';end if;
 insert into calendar_members values(i.calendar_id,auth.uid(),i.role) on conflict(calendar_id,user_id) do update set role=excluded.role;
 update calendar_invitations set status='aceptada' where id=i.id;return i.calendar_id;
end$$;

alter table profiles enable row level security;alter table calendars enable row level security;alter table calendar_members enable row level security;
alter table events enable row level security;alter table calendar_invitations enable row level security;alter table event_history enable row level security;
create policy p_sel on profiles for select using(id=auth.uid() or exists(select 1 from calendar_members a join calendar_members b on a.calendar_id=b.calendar_id where a.user_id=auth.uid() and b.user_id=profiles.id));
create policy p_upd on profiles for update using(id=auth.uid()) with check(id=auth.uid());
create policy c_sel on calendars for select using(has_role(id,array['admin','editor','lector']));
create policy c_ins on calendars for insert with check(owner_id=auth.uid() and type='shared');
create policy m_sel on calendar_members for select using(has_role(calendar_id,array['admin','editor','lector']));
create policy m_upd on calendar_members for update using(has_role(calendar_id,array['admin']) and user_id<>(select owner_id from calendars c where c.id=calendar_id)) with check(has_role(calendar_id,array['admin']));
create policy m_del on calendar_members for delete using(has_role(calendar_id,array['admin']) and user_id<>(select owner_id from calendars c where c.id=calendar_id));
create policy e_sel on events for select using(has_role(calendar_id,array['admin','editor','lector']));
create policy e_ins on events for insert with check(creator_id=auth.uid() and has_role(calendar_id,array['admin','editor']));
create policy e_upd on events for update using(has_role(calendar_id,array['admin','editor'])) with check(has_role(calendar_id,array['admin','editor']));
create policy e_del on events for delete using(has_role(calendar_id,array['admin']) or (has_role(calendar_id,array['editor']) and creator_id=auth.uid()));
create policy i_sel on calendar_invitations for select using(has_role(calendar_id,array['admin']) or lower(email)=lower(auth.jwt()->>'email'));
create policy i_upd on calendar_invitations for update using(has_role(calendar_id,array['admin'])) with check(has_role(calendar_id,array['admin']));
create policy h_sel on event_history for select using(has_role(calendar_id,array['admin']));

revoke all on all tables in schema public from anon,authenticated;
revoke execute on all functions in schema public from public,anon;
grant select on profiles,calendars,calendar_members,events,calendar_invitations,event_history to authenticated;
grant update(name) on profiles to authenticated;
grant insert(id,name,type,owner_id) on calendars to authenticated;
grant insert(calendar_id,creator_id,title,type,status,priority,start_at,end_at,location,description) on events to authenticated;
grant update(title,type,status,priority,start_at,end_at,location,description,modified_at) on events to authenticated;
grant update(role) on calendar_members to authenticated;grant update(status) on calendar_invitations to authenticated;
grant delete on events,calendar_members to authenticated;
grant execute on function has_role,share_event,accept_invitation to authenticated;
alter publication supabase_realtime add table events,calendars,calendar_members,calendar_invitations;
