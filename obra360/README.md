# OBRA360

Estructura: `index.html` + `config.js` (sitio estático), `supabase/migrations/0001_init.sql` (tablas, RLS, funciones) y `supabase/functions/invite` (invitaciones con correo real vía Resend).
Con `config.js` vacío, `index.html` funciona en modo demostración local (ana@obra360.cl / demo123).

## Tu parte, paso a paso
1. **Supabase:** crea un proyecto en supabase.com. Guarda *Project URL* y la clave *anon* (Project Settings → API).
2. **Base de datos:** SQL Editor → pega todo `supabase/migrations/0001_init.sql` → Run.
3. **Auth** (Authentication): en *Sign In / Providers → Email* deja activo *Confirm email*. Esto garantiza que las invitaciones solo las acepte quien controla ese correo. Luego, en *URL Configuration*, pon como *Site URL* la dirección final de tu sitio y añádela también en *Redirect URLs*. Para pruebas puedes usar `http://localhost:8080`.
4. **Correo de Supabase (verificación y recuperación):** el servicio por defecto envía muy pocos correos por hora. En producción configura *Authentication → SMTP* con las credenciales SMTP de Resend.
5. **Resend:** crea cuenta en resend.com, verifica tu dominio (DNS) y crea una API key. Sin dominio verificado solo puedes enviarte correos a ti mismo.
6. **Función de invitaciones** (instala Node y ejecuta en esta carpeta):
```
npm i -g supabase
supabase login
supabase link --project-ref TU_REF
supabase secrets set RESEND_API_KEY=re_xxx FROM_EMAIL="OBRA360 <no-reply@tudominio.cl>" APP_URL=https://tu-sitio.cl/
supabase functions deploy invite
```
`APP_URL` debe ser la URL exacta donde publicarás `index.html`, con `/` final.
7. **Conectar el frontend:** edita `config.js` con tu Project URL y la clave anon. La clave anon es pública por diseño; **nunca** pongas la `service_role` en el frontend.
8. **Publicar:** sube la carpeta (sin `supabase/`) a Netlify, Cloudflare Pages o Vercel. Si cambia la URL, actualiza *Site URL* y `APP_URL`.
9. **Probar en local:** `npx serve -l 8080 .` y abre http://localhost:8080.

## Lista de comprobación (criterios de aceptación)
- Registra dos cuentas; verifica ambos correos; confirma que cada una solo ve su calendario personal.
- Crea un calendario compartido, invita a la segunda cuenta como *Lector* y acepta desde el enlace del correo. Comprueba que no puede crear ni editar.
- Cámbiale el rol a *Editor*; comprueba que los cambios aparecen en las dos sesiones sin recargar.
- Mueve un evento (sale del personal) y copia otro (queda el original); edita la copia y comprueba que el original no cambia.
- Retira al miembro: debe perder el acceso al instante.
- Con la consola del navegador, intenta leer o modificar un calendario ajeno: Supabase debe rechazarlo.

## Funcionalidades y límites conocidos
Incluye todo lo de la especificación para el MVP: cuentas con verificación y recuperación, dos tipos de calendario, vistas mes/semana/día/agenda, eventos con filtros, mover/copiar atómico, invitaciones reales con token hasheado, de un solo uso y con vencimiento de 7 días, roles aplicados con RLS, historial de cambios (`event_history`) y actualización en tiempo real.
No incluye todavía: reenvío con un clic (basta con invitar de nuevo; la anterior se revoca), notificaciones internas y por correo de eventos, recordatorios, editar perfil en pantalla, renombrar o borrar calendarios, ni pruebas automatizadas. Los campos de obra, unidad, técnico y cliente quedan para la etapa 4. No pude ejecutar contra un proyecto real de Supabase desde aquí, así que haz la lista de comprobación antes de producción.
