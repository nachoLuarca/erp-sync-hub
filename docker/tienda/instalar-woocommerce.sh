#!/bin/sh
# Instala WordPress y WooCommerce de forma idempotente y deja la tienda lista para probar el hub.
# Lo ejecuta el servicio `tienda-setup` del perfil `tienda`.
set -eu

: "${WP_ADMIN_PASSWORD:?Define ERPSYNC_WP_ADMIN_PASSWORD (ver docs/variables-de-entorno.md)}"

# Se usa mysqli (el mismo driver de WordPress): el cliente MariaDB de la imagen wp-cli no soporta
# caching_sha2_password, el método de autenticación por defecto de MySQL 8.4.
until php -r 'exit(@mysqli_connect(getenv("WORDPRESS_DB_HOST"), getenv("WORDPRESS_DB_USER"), getenv("WORDPRESS_DB_PASSWORD"), getenv("WORDPRESS_DB_NAME")) ? 0 : 1);'; do
  echo "Esperando a MySQL..."
  sleep 3
done

# wp-config.php lo genera el contenedor `tienda` en el volumen compartido.
until [ -f /var/www/html/wp-config.php ]; do
  echo "Esperando a WordPress..."
  sleep 2
done

if ! wp core is-installed 2>/dev/null; then
  wp core install \
    --url="${WP_URL}" \
    --title="Tienda ERP Sync Hub" \
    --admin_user="${WP_ADMIN_USER}" \
    --admin_password="${WP_ADMIN_PASSWORD}" \
    --admin_email="${WP_ADMIN_EMAIL}" \
    --skip-email
fi

wp plugin is-installed woocommerce || wp plugin install woocommerce
wp plugin is-active woocommerce || wp plugin activate woocommerce
wp rewrite structure '/%postname%/' --hard

# --- Moneda: pesos chilenos, sin decimales ---
wp option update woocommerce_currency CLP
wp option update woocommerce_price_num_decimals 0

# --- Productos de ejemplo con el SKU de artículos del SAP simulado (A00001, A00010...) ---
crear_producto() {
  sku="$1"
  nombre="$2"
  precio="$3"
  if [ -z "$(wp wc product list --user="${WP_ADMIN_USER}" --sku="${sku}" --field=id)" ]; then
    wp wc product create --user="${WP_ADMIN_USER}" --name="${nombre}" --type=simple --sku="${sku}" \
      --regular_price="${precio}" --status=publish --porcelain >/dev/null
    echo "Producto ${sku} creado."
  fi
}

crear_producto A00001 "Taladro percutor 650 W" 39990
crear_producto A00002 "Atornillador inalámbrico 12 V" 29990
crear_producto A00010 "Huincha de medir 5 m" 2990

# --- Webhooks hacia el hub: order.created y order.updated ---
crear_webhook() {
  nombre="$1"
  topic="$2"
  if wp wc webhook list --user="${WP_ADMIN_USER}" --field=name | grep -qxF "${nombre}"; then
    echo "Webhook '${nombre}' ya existe."
    return
  fi

  wp wc webhook create --user="${WP_ADMIN_USER}" --name="${nombre}" --topic="${topic}" \
    --delivery_url="${HUB_WEBHOOK_URL}" --secret="${WC_WEBHOOK_SECRET}" --status=active --porcelain >/dev/null
  echo "Webhook '${nombre}' creado hacia ${HUB_WEBHOOK_URL}."
}

if [ -n "${WC_WEBHOOK_SECRET:-}" ]; then
  crear_webhook "ERP Sync Hub - pedido creado" order.created
  crear_webhook "ERP Sync Hub - pedido actualizado" order.updated
else
  echo "AVISO: ERPSYNC_WC_WEBHOOK_SECRET no está definida; no se crearon los webhooks hacia el hub."
fi

# --- Clave de la API REST para el hub (sincronización de stock y precios) ---
# La clave y el secreto vienen de ERPSYNC_WC_CONSUMER_KEY y ERPSYNC_WC_CONSUMER_SECRET; el registro es idempotente.
if [ -n "${WC_CONSUMER_KEY:-}" ] && [ -n "${WC_CONSUMER_SECRET:-}" ]; then
  wp eval-file /registrar-claves-api.php
else
  echo "AVISO: ERPSYNC_WC_CONSUMER_KEY o ERPSYNC_WC_CONSUMER_SECRET no están definidas; no se registró la clave de API del hub."
fi

echo "Tienda lista en ${WP_URL}"
