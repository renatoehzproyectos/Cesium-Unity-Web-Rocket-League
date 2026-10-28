import {createClient} from 'npm:@supabase/supabase-js@2'
const cors={'Access-Control-Allow-Origin':'*','Access-Control-Allow-Headers':'authorization, x-client-info, apikey, content-type'}
const out=(b:unknown,s=200)=>new Response(JSON.stringify(b),{status:s,headers:{...cors,'Content-Type':'application/json'}})
const hex=(u:Uint8Array)=>[...u].map(b=>b.toString(16).padStart(2,'0')).join('')
const esc=(s:string)=>s.replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]!))
Deno.serve(async req=>{
  if(req.method==='OPTIONS')return new Response('ok',{headers:cors})
  try{
    const url=Deno.env.get('SUPABASE_URL')!
    const asUser=createClient(url,Deno.env.get('SUPABASE_ANON_KEY')!,{global:{headers:{Authorization:req.headers.get('Authorization')??''}}})
    const {data:{user}}=await asUser.auth.getUser()
    if(!user)return out({error:'No autenticado'},401)
    const {calendar_id,email:raw,role}=await req.json()
    const email=String(raw).trim().toLowerCase()
    if(!/^\S+@\S+\.\S+$/.test(email)||!['admin','editor','lector'].includes(role))return out({error:'Datos no válidos'},400)
    const {data:mem}=await asUser.from('calendar_members').select('role').eq('calendar_id',calendar_id).eq('user_id',user.id).maybeSingle()
    const {data:cal}=await asUser.from('calendars').select('name,type').eq('id',calendar_id).maybeSingle()
    if(!cal||cal.type!=='shared'||mem?.role!=='admin')return out({error:'Solo los administradores pueden invitar'},403)
    const db=createClient(url,Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!)
    const {data:p}=await db.from('profiles').select('id').eq('email',email).maybeSingle()
    if(p){const {data:m}=await db.from('calendar_members').select('user_id').eq('calendar_id',calendar_id).eq('user_id',p.id).maybeSingle();if(m)return out({error:'Esa persona ya es miembro'},409)}
    const token=hex(crypto.getRandomValues(new Uint8Array(32)))
    const hash=hex(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(token))))
    await db.from('calendar_invitations').update({status:'revocada'}).eq('calendar_id',calendar_id).eq('email',email).eq('status','pendiente')
    const {data:inv,error}=await db.from('calendar_invitations').insert({calendar_id,calendar_name:cal.name,email,role,invited_by:user.id,invited_by_email:user.email,token_hash:hash,expires_at:new Date(Date.now()+7*864e5).toISOString()}).select('id').single()
    if(error)return out({error:error.message},500)
    const link=`${Deno.env.get('APP_URL')}#invite=${token}`
    const names:Record<string,string>={admin:'Administrador',editor:'Editor',lector:'Lector'}
    const r=await fetch('https://api.resend.com/emails',{method:'POST',headers:{Authorization:`Bearer ${Deno.env.get('RESEND_API_KEY')}`,'Content-Type':'application/json'},
      body:JSON.stringify({from:Deno.env.get('FROM_EMAIL'),to:email,subject:`${user.email} te invitó a «${cal.name}» en OBRA360`,
      html:`<p><b>${esc(user.email??'')}</b> te invitó al calendario <b>${esc(cal.name)}</b> con el rol <b>${names[role]}</b>.</p><p><a href="${link}">Aceptar invitación</a></p><p>El enlace vence en 7 días. Debes entrar con este mismo correo (${esc(email)}).</p>`})})
    if(!r.ok){await db.from('calendar_invitations').delete().eq('id',inv.id);return out({error:'El proveedor de correo rechazó el envío'},502)}
    return out({ok:true})
  }catch(e){return out({error:String(e)},500)}
})
