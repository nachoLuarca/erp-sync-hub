<?php
/**
 * Plugin Name: Permitir la API REST de WooCommerce por HTTP
 * Description: SOLO DESARROLLO LOCAL. No instalar en una tienda real.
 *
 * WooCommerce acepta la clave y el secreto de la API (autenticación Basic) únicamente por HTTPS. En Docker Compose el hub
 * llama a la tienda por la red interna, en HTTP plano y sin salir de la máquina, así que este plugin hace que WordPress trate
 * como seguras las peticiones a la API REST de este entorno.
 *
 * En producción la tienda se sirve por HTTPS y no hace falta nada de esto.
 */

defined( 'ABSPATH' ) || exit;

if ( isset( $_SERVER['REQUEST_URI'] ) && false !== strpos( (string) $_SERVER['REQUEST_URI'], '/wp-json/' ) ) { // phpcs:ignore WordPress.Security.ValidatedSanitizedInput
	$_SERVER['HTTPS'] = 'on';
}
