<?php
/**
 * Plugin Name: Permitir webhooks hacia el hub local
 * Description: SOLO DESARROLLO LOCAL. No instalar en una tienda real.
 *
 * WordPress rechaza por seguridad los webhooks hacia direcciones privadas y puertos no estándar, que es justo donde corre
 * el hub en Docker Compose. Este plugin lo permite únicamente para los hosts de desarrollo indicados y hace la entrega
 * síncrona, para que la prueba de punta a punta no dependa de WP-Cron ni del tráfico de la tienda.
 *
 * En producción, la URL del webhook es pública y HTTPS, y no hace falta nada de esto.
 */

defined( 'ABSPATH' ) || exit;

const ERP_SYNC_HUB_DEV_HOSTS = array( 'hub', 'host.docker.internal', 'localhost' );

// 1. Permite resolver estos hosts aunque apunten a una IP privada (wp_safe_remote_request los bloquea por defecto).
add_filter(
	'http_request_host_is_external',
	static function ( $is_external, $host ) {
		return in_array( $host, ERP_SYNC_HUB_DEV_HOSTS, true ) ? true : $is_external;
	},
	10,
	2
);

// 2. Permite los puertos del hub (por defecto WordPress solo admite 80, 443 y 8080).
add_filter(
	'http_allowed_safe_ports',
	static function ( $ports ) {
		return array_unique( array_merge( (array) $ports, array( 5210, 8080 ) ) );
	}
);

// 3. Entrega inmediata en lugar de encolarla en Action Scheduler.
add_filter( 'woocommerce_webhook_deliver_async', '__return_false' );
