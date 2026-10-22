#!/bin/sh
# Instala WordPress y WooCommerce de forma idempotente. Lo ejecuta el servicio `tienda-setup` del perfil `tienda`.
set -eu

: "${WP_ADMIN_PASSWORD:?Define WP_ADMIN_PASSWORD en .env (ver .env.example)}"

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
echo "Tienda lista en ${WP_URL}"
