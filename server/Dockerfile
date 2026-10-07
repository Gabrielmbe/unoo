# =============================================================================
#  Imagen mínima para el servidor autoritativo.
#  `node:20-alpine` y no `slim`: ~50 MB menos de imagen = despliegues más rápidos
#  y menos superficie de ataque. En un free/starter tier eso se nota.
# =============================================================================
FROM node:20-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
# Sólo dependencias de producción: ws y nada más.
RUN npm ci --omit=dev

FROM node:20-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=deps /app/node_modules ./node_modules
COPY package.json ./
COPY src ./src

# Usuario sin privilegios: buenas prácticas en cualquier contenedor público.
USER node

# Render inyecta PORT; este EXPOSE es sólo documentación.
EXPOSE 10000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:${PORT:-10000}/health || exit 1

CMD ["node", "src/index.js"]
