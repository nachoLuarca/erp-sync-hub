<?php
/**
 * Registra en WooCommerce la clave de la API REST que usa el hub para sincronizar stock y precios.
 *
 * Lo ejecuta tienda-setup con `wp eval-file`. La clave y el secreto no se generan aquí: vienen de las variables de entorno
 * ERPSYNC_WC_CONSUMER_KEY y ERPSYNC_WC_CONSUMER_SECRET, que son su única fuente de verdad (el hub las lee como
 * Store__WooCommerce__ConsumerKey y Store__WooCommerce__ConsumerSecret). Es idempotente: si la clave ya está registrada, se
 * actualiza en lugar de duplicarse.
 *
 * WooCommerce guarda la clave como hash HMAC-SHA256 (wc_api_hash) y el secreto tal cual; aquí se hace igual que en
 * WC_AJAX::update_api_key, para que la API acepte las credenciales como si se hubieran creado desde el administrador.
 */

defined( 'ABSPATH' ) || exit;

$consumer_key    = (string) getenv( 'WC_CONSUMER_KEY' );
$consumer_secret = (string) getenv( 'WC_CONSUMER_SECRET' );
$admin_login     = (string) getenv( 'WP_ADMIN_USER' );

if ( ! preg_match( '/^ck_[0-9a-f]{40}$/', $consumer_key ) ) {
	WP_CLI::error( 'ERPSYNC_WC_CONSUMER_KEY debe tener el formato ck_ seguido de 40 caracteres hexadecimales (ver docs/variables-de-entorno.md).' );
}

if ( ! preg_match( '/^cs_[0-9a-f]{40}$/', $consumer_secret ) ) {
	WP_CLI::error( 'ERPSYNC_WC_CONSUMER_SECRET debe tener el formato cs_ seguido de 40 caracteres hexadecimales (ver docs/variables-de-entorno.md).' );
}

$user = get_user_by( 'login', $admin_login );
if ( ! $user ) {
	WP_CLI::error( "No existe el usuario administrador '{$admin_login}' para asociarle la clave de API." );
}

global $wpdb;

$table = $wpdb->prefix . 'woocommerce_api_keys';
$hash  = wc_api_hash( $consumer_key );
$row   = array(
	'user_id'         => $user->ID,
	'description'     => 'ERP Sync Hub',
	'permissions'     => 'read_write',
	'consumer_key'    => $hash,
	'consumer_secret' => $consumer_secret,
	'truncated_key'   => substr( $consumer_key, -7 ),
);

$existing_id = $wpdb->get_var( $wpdb->prepare( "SELECT key_id FROM {$table} WHERE consumer_key = %s", $hash ) ); // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared

if ( $existing_id ) {
	$wpdb->update( $table, $row, array( 'key_id' => (int) $existing_id ) );
	WP_CLI::success( 'La clave de API del hub ya estaba registrada; se actualizó.' );
	return;
}

if ( false === $wpdb->insert( $table, $row ) ) {
	WP_CLI::error( 'No se pudo registrar la clave de API: ' . $wpdb->last_error );
}

WP_CLI::success( 'Clave de API del hub registrada con permiso de lectura y escritura.' );
